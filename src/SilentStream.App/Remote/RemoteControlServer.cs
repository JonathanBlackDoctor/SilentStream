using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Logging;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using SilentStream.Core.Remote.WebPush;
using SilentStream.Core.YouTube;
using SilentStream.App.Recovery;

namespace SilentStream.App.Remote;

/// <summary>
/// Embedded ASP.NET Core (Kestrel) remote-control server (확장계획서 §4.4/§7). Serves a single
/// responsive mobile page and a token-authenticated REST + WebSocket API for editing the
/// timetable, toggling the live stream, and watching status. PIN pairing issues per-device
/// tokens whose SHA-256 hashes live in config (D11). Bind mode: off / lan / tailscale (D8).
/// </summary>
public sealed class RemoteControlServer : IRemoteControlServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<string, DayOfWeek> WeekdayMap = new Dictionary<string, DayOfWeek>
    {
        ["Sun"] = DayOfWeek.Sunday, ["Mon"] = DayOfWeek.Monday, ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday, ["Thu"] = DayOfWeek.Thursday, ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday
    };

    private readonly IStreamOrchestrator _orchestrator;
    private readonly IPeriodScheduleStore _scheduleStore;
    private readonly PeriodScheduler _scheduler;
    private readonly ISplitApprovalService _splits;
    private readonly IUploadQueue _uploadQueue;
    private readonly IPeriodAssetCatalog _periodAssets;
    private readonly IYouTubeCaptionService _captions;
    private readonly IRecordingManager _recording;
    private readonly IAudioMixer _audioMixer;
    private readonly IPreviewProvider _preview;
    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _tokenProtector;
    private readonly IHealthMonitor _health;
    private readonly IAdaptiveQualityController _quality;
    private readonly IPushSubscriptionStore _pushSubscriptions;
    private readonly IVapidKeyStore _vapidKeys;
    private readonly RemoteAppRestartService _appRestart;
    private readonly RemoteSystemShutdownService _systemShutdown;
    private readonly RemoteUninstallService _appUninstall;
    private readonly RecoveryCoordinator _recovery;
    private readonly ILogService _log;

    private readonly object _socketsGate = new();
    private readonly List<SocketChannel> _sockets = [];
    private readonly object _silentMicsGate = new();
    private readonly Dictionary<string, string> _silentMics = new();
    private readonly string _html;
    // PWA 자산(원격 컨트롤러 개선 Phase 3): index.html과 함께 어셈블리에 임베드, 무인증 서빙.
    private readonly string _manifest;
    private readonly string _serviceWorker;
    private readonly string _iconSvg;
    private readonly byte[] _icon192;
    private readonly byte[] _icon512;
    private readonly byte[] _iconMaskable;

    private readonly CloudflaredManager _cloudflared;
    private readonly PairingThrottle _pairThrottle = new();
    private WebApplication? _app;
    private int? _firewallPort; // the port a runtime firewall rule was opened for (LAN mode), to remove on stop
    private MetricsSnapshot _lastMetrics = MetricsSnapshot.Empty;
    private int _levelTick;

    // Room label (호실명) cached at server start so the phone can show which PC it controls without a
    // config file read on every status push. It changes only when the operator edits settings (rare),
    // so reflecting it on the next app start is acceptable.
    private string _roomName = string.Empty;

    public RemoteControlServer(
        IStreamOrchestrator orchestrator,
        IPeriodScheduleStore scheduleStore,
        PeriodScheduler scheduler,
        ISplitApprovalService splits,
        IUploadQueue uploadQueue,
        IPeriodAssetCatalog periodAssets,
        IYouTubeCaptionService captions,
        IRecordingManager recording,
        IAudioMixer audioMixer,
        IPreviewProvider preview,
        IConfigStore configStore,
        ITokenProtector tokenProtector,
        IHealthMonitor health,
        IAdaptiveQualityController quality,
        IPushSubscriptionStore pushSubscriptions,
        IVapidKeyStore vapidKeys,
        RemoteAppRestartService appRestart,
        RemoteSystemShutdownService systemShutdown,
        RemoteUninstallService appUninstall,
        RecoveryCoordinator recovery,
        ILogService log)
    {
        _orchestrator = orchestrator;
        _scheduleStore = scheduleStore;
        _scheduler = scheduler;
        _splits = splits;
        _uploadQueue = uploadQueue;
        _periodAssets = periodAssets;
        _captions = captions;
        _recording = recording;
        _audioMixer = audioMixer;
        _preview = preview;
        _configStore = configStore;
        _tokenProtector = tokenProtector;
        _health = health;
        _quality = quality;
        _pushSubscriptions = pushSubscriptions;
        _vapidKeys = vapidKeys;
        _appRestart = appRestart;
        _systemShutdown = systemShutdown;
        _appUninstall = appUninstall;
        _recovery = recovery;
        _log = log;
        _cloudflared = new CloudflaredManager(log);
        _html = LoadEmbeddedHtml();
        _manifest = LoadEmbeddedText("manifest.webmanifest");
        _serviceWorker = LoadEmbeddedText("service-worker.js");
        _iconSvg = LoadEmbeddedText("icon.svg");
        _icon192 = LoadEmbeddedBytes("icon-192.png");
        _icon512 = LoadEmbeddedBytes("icon-512.png");
        _iconMaskable = LoadEmbeddedBytes("icon-maskable.png");
    }

    /// <summary>Current pairing PIN to show in the control window (null until the server starts).</summary>
    public string? CurrentPin { get; private set; }

    /// <summary>Raised when a new pairing PIN is generated (server start).</summary>
    public event Action<string>? PinChanged;

    /// <summary>The Cloudflare-tunnel public URL phones use, once known (null unless mode=cloudflare).</summary>
    public string? CurrentPublicUrl { get; private set; }

    /// <summary>Raised when the Cloudflare tunnel reports its public URL.</summary>
    public event Action<string>? PublicUrlChanged;

    public async Task StartAsync(RemoteBindMode mode, int port, CancellationToken ct)
    {
        if (mode == RemoteBindMode.Off)
        {
            _log.Info("원격 제어가 꺼져 있습니다(remote.mode=off).");
            return;
        }
        if (_app is not null)
        {
            return; // already running
        }

        var bindIp = ResolveBindAddress(mode);
        if (bindIp is null)
        {
            _log.Warn("Tailscale 인터페이스 IP를 찾지 못했습니다. Tailscale 연결 후 다시 시도하세요 (원격 미기동).");
            return;
        }

        if (mode == RemoteBindMode.Lan)
        {
            TryConfigureFirewall(port);
        }

        RotatePin();
        _roomName = _configStore.Load().DeviceName ?? string.Empty;

        var url = $"http://{bindIp}:{port}";
        var displayHost = mode == RemoteBindMode.Lan ? FirstLanIpv4() ?? bindIp : bindIp;
        _log.Info($"원격 제어 서버 시작: {url}  —  폰 브라우저로 http://{displayHost}:{port} 접속 후 제어창에 표시된 PIN을 입력하세요.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();

        app.UseWebSockets();
        ConfigureCors(app); // must run before auth: preflights carry no token (Phase 2 멀티 호실)
        ConfigureAuth(app);
        MapEndpoints(app);

        _orchestrator.StateChanged += OnStateOrMetricsChanged;
        _orchestrator.MetricsUpdated += OnMetrics;
        _orchestrator.QualityChanged += OnQualityChanged;
        _audioMixer.LevelsUpdated += OnLevels;
        _audioMixer.MicSignalChanged += OnMicSignal;
        _health.HealthChanged += OnHealthChanged;
        _preview.FrameUpdated += OnPreviewFrame;
        _splits.Changed += OnSplitsChanged; // 승인 카드 실시간 갱신 (생성/승인/연강/자동승인/컷 완료)

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;

        if (mode == RemoteBindMode.Cloudflare)
        {
            await StartCloudflareTunnelAsync(port, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings up the bundled cloudflared tunnel pointing at the loopback server and publishes its
    /// public URL. A named tunnel (token + hostname in config) gives a stable URL; otherwise a quick
    /// tunnel yields a random *.trycloudflare.com URL. Failure never blocks the live/recording path.
    /// </summary>
    private async Task StartCloudflareTunnelAsync(int port, CancellationToken ct)
    {
        var remote = _configStore.Load().Remote;
        var token = ResolveCloudflareToken(remote);
        var hostname = string.IsNullOrWhiteSpace(remote.CloudflareHostname) ? null : remote.CloudflareHostname;
        _log.Info($"Cloudflare 터널을 시작합니다(전송 프로토콜: {remote.CloudflareProtocol}).");
        try
        {
            var publicUrl = await _cloudflared
                .StartAsync(token, port, hostname, remote.CloudflareProtocol, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(publicUrl))
            {
                CurrentPublicUrl = publicUrl;
                PublicUrlChanged?.Invoke(publicUrl);
                _log.Info($"Cloudflare 터널 공개 주소: {publicUrl}  —  폰 브라우저로 접속 후 제어창에 표시된 PIN을 입력하세요.");
            }
            else
            {
                _log.Info("Cloudflare 명명형 터널을 시작했습니다(공개 주소는 대시보드에 매핑한 호스트네임).");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Cloudflare 터널 시작 실패(라이브/녹화에는 영향 없음)", ex);
        }
    }

    /// <summary>
    /// Resolves the plaintext named-tunnel token for cloudflared, keeping it encrypted at rest.
    /// A freshly pasted <see cref="RemoteConfig.CloudflareTunnelToken"/> is DPAPI-encrypted into
    /// <see cref="RemoteConfig.CloudflareTunnelTokenEnc"/> and the plaintext field is wiped on disk;
    /// thereafter the encrypted form is decrypted for use. Returns null (quick tunnel) when no token
    /// is configured or a crypto step fails — the tunnel start path treats that as non-fatal.
    /// </summary>
    private string? ResolveCloudflareToken(RemoteConfig remote)
    {
        // Freshly pasted plaintext: encrypt at rest, then wipe the plaintext copy from config.json.
        if (!string.IsNullOrWhiteSpace(remote.CloudflareTunnelToken))
        {
            var plain = remote.CloudflareTunnelToken;
            try
            {
                var enc = _tokenProtector.Protect(plain);
                _configStore.Update(c =>
                {
                    c.Remote.CloudflareTunnelTokenEnc = enc;
                    c.Remote.CloudflareTunnelToken = string.Empty;
                });
                _log.Info("Cloudflare 터널 토큰을 DPAPI로 암호화 저장했습니다(평문 제거).");
            }
            catch (Exception ex)
            {
                _log.Error("Cloudflare 터널 토큰 암호화 실패 — 이번 실행은 평문 토큰으로 진행합니다.", ex);
            }
            return plain;
        }

        // Already encrypted at rest: decrypt for use.
        if (!string.IsNullOrWhiteSpace(remote.CloudflareTunnelTokenEnc))
        {
            try
            {
                return _tokenProtector.Unprotect(remote.CloudflareTunnelTokenEnc);
            }
            catch (Exception ex)
            {
                _log.Error("Cloudflare 터널 토큰 복호화 실패 — 명명형 토큰 없이 진행합니다.", ex);
                return null;
            }
        }

        return null;
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null)
        {
            return;
        }
        _app = null;
        _splits.Changed -= OnSplitsChanged;
        _orchestrator.StateChanged -= OnStateOrMetricsChanged;
        _orchestrator.MetricsUpdated -= OnMetrics;
        _orchestrator.QualityChanged -= OnQualityChanged;
        _audioMixer.LevelsUpdated -= OnLevels;
        _audioMixer.MicSignalChanged -= OnMicSignal;
        _health.HealthChanged -= OnHealthChanged;
        _preview.FrameUpdated -= OnPreviewFrame;

        lock (_socketsGate)
        {
            _sockets.Clear();
        }

        await _cloudflared.StopAsync().ConfigureAwait(false);
        CurrentPublicUrl = null;
        RemoveFirewall(); // B4: don't leave the inbound port open after the app stops

        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"원격 서버 정지 중: {ex.Message}");
        }
        await app.DisposeAsync().ConfigureAwait(false);
        _log.Info("원격 제어 서버를 정지했습니다.");
    }

    /// <summary>Generates a fresh pairing PIN and notifies the control window.</summary>
    private void RotatePin()
    {
        CurrentPin = RemoteAuth.NewPin();
        PinChanged?.Invoke(CurrentPin);
    }

    /// <summary>Removes the runtime inbound firewall rule this server added (LAN mode), if any.</summary>
    private void RemoveFirewall()
    {
        if (_firewallPort is not int port)
        {
            return;
        }
        _firewallPort = null;
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"MediaCaptureHelper Remote {port}\"");
            _log.Info($"원격 방화벽 인바운드 규칙을 제거했습니다(TCP {port}).");
        }
        catch (Exception ex)
        {
            _log.Debug($"방화벽 규칙 제거 실패(다음 시작 시 재정리됨): {ex.Message}");
        }
    }

    // ---- CORS (Phase 2 멀티 호실: 폰이 다른 호실 출처의 페이지에서 이 API를 직접 호출) ----

    /// <summary>
    /// Adds CORS headers for cross-origin <c>/api</c> calls and answers preflights. Placed
    /// <b>before</b> <see cref="ConfigureAuth"/> for two reasons: an OPTIONS preflight carries no
    /// token so it must short-circuit here (204), and headers set here survive onto the auth
    /// middleware's 401 so the phone can distinguish "token expired" from a network error.
    /// The Origin is reflected per request (no wildcard) and token auth is not relaxed at all —
    /// CORS only lets the browser read responses it was already authorized to receive.
    /// </summary>
    private static void ConfigureCors(WebApplication app) =>
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var path = ctx.Request.Path.Value ?? string.Empty;
            if (RemoteCors.AppliesTo(origin, path))
            {
                var headers = ctx.Response.Headers;
                headers.AccessControlAllowOrigin = origin;
                headers.AccessControlAllowHeaders = RemoteCors.AllowHeaders;
                headers.AccessControlAllowMethods = RemoteCors.AllowMethods;
                headers.AccessControlExposeHeaders = RemoteCors.ExposeHeaders;
                headers.AccessControlMaxAge = RemoteCors.MaxAgeSeconds;
                headers.Vary = "Origin"; // reflected origin → responses must not be cross-origin cached

                if (RemoteCors.IsPreflight(ctx.Request.Method, origin, path))
                {
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            }
            await next().ConfigureAwait(false);
        });

    // ---- auth ----

    private void ConfigureAuth(WebApplication app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var protectedPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");
            var isPairing = path.StartsWithSegments("/api/pair");
            if (!protectedPath || isPairing)
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (!RemoteAuth.IsKnownToken(KnownTokenHashes(), ExtractToken(ctx)))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next().ConfigureAwait(false);
        });

    private List<string> KnownTokenHashes() => _configStore.Load().Remote.DeviceTokens;

    private static string? ExtractToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }
        if (ctx.Request.Headers.TryGetValue("X-Device-Token", out var header))
        {
            return header.ToString();
        }
        return ctx.Request.Query.TryGetValue("token", out var q) ? q.ToString() : null;
    }

    // ---- endpoints (확장계획서 §7) ----

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(_html, "text/html; charset=utf-8"));

        // PWA 설치 자산(원격 컨트롤러 개선 Phase 3). /api·/ws 밖이라 무인증 서빙 — 페어링 전에도 브라우저가
        // 매니페스트/서비스워커/아이콘을 로드할 수 있어야 한다. 서비스워커는 루트에서 서빙되어 스코프가 "/".
        app.MapGet("/manifest.webmanifest", () =>
            Results.Content(_manifest, "application/manifest+json; charset=utf-8"));
        app.MapGet("/service-worker.js", () =>
            Results.Content(_serviceWorker, "text/javascript; charset=utf-8"));
        app.MapGet("/icon.svg", () => Results.Content(_iconSvg, "image/svg+xml"));
        app.MapGet("/icon-192.png", () => Results.Bytes(_icon192, "image/png"));
        app.MapGet("/icon-512.png", () => Results.Bytes(_icon512, "image/png"));
        app.MapGet("/icon-maskable.png", () => Results.Bytes(_iconMaskable, "image/png"));

        app.MapPost("/api/pair", async ctx =>
        {
            // Brute-force guard (B3): the PIN is only 6 digits, so reject without checking once
            // too many attempts have failed, and use a constant-time compare below.
            if (_pairThrottle.IsLocked(out var retryAfter))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await ctx.Response.WriteAsJsonAsync(new { error = "잠시 후 다시 시도하세요." }).ConfigureAwait(false);
                return;
            }

            var body = await ReadJsonAsync<PairRequest>(ctx).ConfigureAwait(false);
            if (body?.Pin is null || CurrentPin is null || !RemoteAuth.ConstantTimeEquals(body.Pin, CurrentPin))
            {
                if (_pairThrottle.RecordFailure())
                {
                    // Rotate the PIN on lockout so any guesses an attacker made are discarded.
                    RotatePin();
                    _log.Warn("페어링 PIN 시도가 반복 실패해 일시적으로 잠그고 PIN을 회전했습니다(무차별 대입 방어).");
                }
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "PIN이 올바르지 않습니다." }).ConfigureAwait(false);
                return;
            }

            _pairThrottle.RecordSuccess();
            var token = RemoteAuth.NewToken();
            // Atomic append so a just-paired token can't be clobbered by a concurrent config save.
            _configStore.Update(config => config.Remote.DeviceTokens.Add(RemoteAuth.HashToken(token)));
            _log.Info("새 기기가 페어링되었습니다(기기 토큰 발급).");
            await ctx.Response.WriteAsJsonAsync(new { token }).ConfigureAwait(false);
        });

        app.MapGet("/api/status", () => Results.Json(BuildStatus(), Json));
        app.MapGet("/api/diagnostics", () => Results.Json(BuildDiagnostics(DateTime.Now), Json));
        app.MapGet("/api/recovery/status", () => Results.Json(_recovery.Status, Json));

        // Download assets are deliberately catalogued independently from the short-lived upload
        // queue. A completed queue job is pruned after a while, but its local audio remains
        // available to the paired operator. No caller-controlled file path is ever accepted.
        app.MapGet("/api/period-assets", () => Results.Json(BuildPeriodAssets(), Json));
        app.MapGet("/api/period-assets/{id}/audio", (string id) =>
        {
            var asset = _periodAssets.Find(id);
            var audioPath = asset is null ? null : GetSafeAssetAudioPath(asset.AudioPath);
            if (asset is null || audioPath is null)
            {
                return Results.Json(new { error = "다운로드할 로컬 음성 파일이 없습니다." }, Json,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.File(audioPath, "audio/mp4", PeriodAssetDownloadName(asset, "m4a"),
                enableRangeProcessing: true);
        });
        app.MapGet("/api/period-assets/{id}/captions", async Task<IResult> (string id, HttpContext context) =>
        {
            var asset = _periodAssets.Find(id);
            if (asset is null)
            {
                return Results.Json(new { error = "교시 자료를 찾을 수 없습니다." }, Json,
                    statusCode: StatusCodes.Status404NotFound);
            }
            if (string.IsNullOrWhiteSpace(asset.VideoId))
            {
                return Results.Json(new { error = "YouTube 업로드가 완료된 뒤 자막을 받을 수 있습니다." }, Json,
                    statusCode: StatusCodes.Status409Conflict);
            }

            UpdateCaptionStatusSafely(asset.Id, PeriodAssetCaptionStatus.Downloading);
            try
            {
                // YouTube retains the source caption track; we only pass the requested SRT through
                // to this authorized request and do not cache its text locally.
                var caption = await _captions
                    .DownloadPreferredSrtAsync(asset.VideoId, "ko", context.RequestAborted)
                    .ConfigureAwait(false);
                if (caption is null)
                {
                    UpdateCaptionStatusSafely(asset.Id, PeriodAssetCaptionStatus.Unavailable,
                        message: "YouTube 자막이 아직 준비되지 않았습니다.");
                    return Results.Json(new { error = "YouTube 자막이 아직 준비되지 않았습니다." }, Json,
                        statusCode: StatusCodes.Status404NotFound);
                }

                UpdateCaptionStatusSafely(asset.Id, PeriodAssetCaptionStatus.Available, caption.Language);
                return Results.File(caption.Srt, "application/x-subrip; charset=utf-8",
                    PeriodAssetDownloadName(asset, "srt"));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"교시 자막 다운로드 실패({asset.PeriodNumber}교시): {ex.Message}");
                UpdateCaptionStatusSafely(asset.Id, PeriodAssetCaptionStatus.Failed,
                    message: "YouTube 자막 권한 또는 처리 상태를 확인해주세요.");
                return Results.Json(new { error = "자막을 받지 못했습니다. PC에서 YouTube 자막 권한을 승인했는지 확인해주세요." }, Json,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // 멀티 호실 그리드 카드용 경량 요약(Phase 2). 시간표/업로드 큐 등 무거운 부분은 /api/status에만 둔다.
        app.MapGet("/api/summary", () => Results.Json(BuildSummary(), Json));

        // This endpoint remains reachable in every mode so the operator can deliberately change
        // the lock after the confirmation shown by the remote UI. Each protected write below is
        // still checked on the server, so an old or hand-written client cannot bypass the lock.
        app.MapGet("/api/settings-lock", () => Results.Json(BuildSettingsLock(), Json));
        app.MapPut("/api/settings-lock", async Task<IResult> (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync<SettingsLockRequest>(ctx).ConfigureAwait(false);
            if (!RemoteSettingsLock.TryParse(body?.Mode, out var mode))
            {
                return Results.Json(new { ok = false, error = "알 수 없는 설정 잠금 모드입니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            _configStore.Update(config => config.Remote.SettingsLockMode = mode);
            _log.Info($"원격 설정 잠금 모드가 '{mode}'(으)로 변경되었습니다.");
            return Results.Json(new { ok = true, settingsLock = BuildSettingsLock() }, Json);
        });

        app.MapGet("/api/automation", () => Results.Json(BuildAutomation(), Json));
        app.MapPut("/api/automation", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }

            var body = await ReadJsonAsync<AutomationRequest>(ctx).ConfigureAwait(false);
            if (body?.Enabled is not bool enabled)
            {
                return Results.Json(new { ok = false, error = "자동 운행 설정이 올바르지 않습니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            _configStore.Update(config => config.AutomaticOperationEnabled = enabled);
            if (!enabled)
            {
                await _orchestrator.StopAsync().ConfigureAwait(false);
            }

            _log.Warn(enabled
                ? "원격 요청으로 자동 녹화·방송을 다음 앱 시작부터 다시 켰습니다."
                : "원격 요청으로 자동 녹화·방송을 중지했습니다.");
            return Results.Json(new { ok = true, automation = BuildAutomation() }, Json);
        });

        app.MapGet("/api/schedule", () => Results.Json(GetWeekdayDefaults(), Json));
        app.MapPut("/api/schedule", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Schedule) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<Dictionary<string, List<PeriodDto>>>(ctx).ConfigureAwait(false);
            if (body is null)
            {
                return Results.Json(new { ok = false, error = "시간표 형식이 올바르지 않습니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            foreach (var (key, rows) in body)
            {
                if (WeekdayMap.TryGetValue(key, out var day))
                {
                    _scheduleStore.SetWeekdayDefault(day, ToDaySchedule(rows));
                }
            }
            _scheduler.NotifyScheduleChanged();
            _log.Info("요일별 기본 시간표가 갱신되었습니다(원격).");
            return Results.Json(new { ok = true }, Json);
        });

        app.MapGet("/api/schedule/today", () =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return Results.Json(new
            {
                date = today.ToString("yyyy-MM-dd"),
                hasOverride = _scheduleStore.GetOverride(today) is not null,
                periods = ToDtos(_scheduleStore.ResolveForDate(today))
            }, Json);
        });
        app.MapPut("/api/schedule/today", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Schedule) is { } blocked)
            {
                return blocked;
            }
            var rows = await ReadJsonAsync<List<PeriodDto>>(ctx).ConfigureAwait(false) ?? [];
            var today = DateOnly.FromDateTime(DateTime.Now);
            _scheduleStore.SetOverride(today, ToDaySchedule(rows));
            _scheduler.NotifyScheduleChanged();
            _log.Info($"오늘({today:yyyy-MM-dd}) 시간표 덮어쓰기가 적용되었습니다(원격).");
            return Results.Json(new { ok = true }, Json);
        });
        app.MapDelete("/api/schedule/today", () =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Schedule) is { } blocked)
            {
                return blocked;
            }
            var today = DateOnly.FromDateTime(DateTime.Now);
            _scheduleStore.ClearOverride(today);
            _scheduler.NotifyScheduleChanged();
            _log.Info($"오늘({today:yyyy-MM-dd}) 시간표 덮어쓰기를 해제했습니다(원격).");
            return Results.Json(new { ok = true }, Json);
        });

        // ---- 승인 기반 교시 분할 (v8): 승인 카드 조회 + 승인/연강/건너뛰기/연강 취소 ----
        app.MapGet("/api/splits", () => Results.Json(BuildSplits(), Json));
        app.MapPost("/api/splits/cancel-merge", () =>
        {
            var result = SplitRemote.CancelMerge(_splits, BuildSplits);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });
        app.MapPost("/api/splits/{id}/approve", async (string id, HttpContext ctx) =>
        {
            var body = await ReadJsonAsync<SplitRemote.ApproveRequest>(ctx).ConfigureAwait(false);
            var result = SplitRemote.Approve(_splits, id, body, BuildSplits);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });
        app.MapPost("/api/splits/{id}/merge", (string id) =>
        {
            var result = SplitRemote.Merge(_splits, id, BuildSplits);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });
        app.MapPost("/api/splits/{id}/skip", (string id) =>
        {
            var result = SplitRemote.Skip(_splits, id, BuildSplits);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });

        // ---- audio mixer (다중 채널 + 증폭 + 실시간 미터) ----
        app.MapGet("/api/audio", () => Results.Json(BuildAudio(), Json));
        app.MapPost("/api/audio/mute", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<AudioMuteRequest>(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body?.Id))
            {
                return Results.Json(new { ok = false, error = "오디오 입력을 찾을 수 없습니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            _audioMixer.SetMuted(body.Id, body.Muted);
            PersistSourceChange(body.Id, s => s.Muted = body.Muted);
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPost("/api/audio/gain", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<AudioGainRequest>(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body?.Id))
            {
                return Results.Json(new { ok = false, error = "오디오 입력을 찾을 수 없습니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var gain = Math.Clamp(body.Gain, 0, 4);
            _audioMixer.SetGain(body.Id, gain);
            PersistSourceChange(body.Id, s => s.Gain = gain);
            return Results.Json(new { ok = true }, Json);
        });

        // ---- 송출 품질 (적응형 품질 §7.2: 조회 + 수동 고정/자동 복귀) ----
        app.MapGet("/api/quality", () => Results.Json(
            QualityRemote.BuildDto(_quality, _orchestrator.CurrentQuality,
                _configStore.Load().Encoding.Adaptive.Enabled), Json));
        app.MapPut("/api/quality", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<QualityRemote.PutRequest>(ctx).ConfigureAwait(false);
            var result = QualityRemote.Apply(body, _quality, _orchestrator.CurrentQuality,
                _configStore.Load().Encoding.Adaptive.Enabled,
                live: _orchestrator.State == StreamState.Live);
            if (result.Ok)
            {
                _log.Info($"원격 품질 요청: mode={result.Quality.Mode}, level={result.Quality.Level} → {result.Applied}");
            }
            return Results.Json(result, Json,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        // ---- 폰 PWA Web Push 구독 (원격 컨트롤러 개선 Phase 3) ----
        // /api 아래라 토큰 게이트 뒤 — 페어링된 기기만 구독한다. VAPID 공개키는 비밀이 아니지만 구독이
        // 페어링 후에만 의미 있으므로 같은 게이트를 공유한다.
        app.MapGet("/api/push/vapid", () => Results.Json(PushRemote.GetVapid(_vapidKeys), Json));
        app.MapPost("/api/push/subscribe", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<PushRemote.SubscribeRequest>(ctx).ConfigureAwait(false);
            var result = PushRemote.Subscribe(_pushSubscriptions, body);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });
        app.MapDelete("/api/push/subscribe", async Task<IResult> (HttpContext ctx) =>
        {
            if (BlockSettingsChange(RemoteSettingsSection.Other) is { } blocked)
            {
                return blocked;
            }
            var body = await ReadJsonAsync<PushRemote.SubscribeRequest>(ctx).ConfigureAwait(false);
            var result = PushRemote.Unsubscribe(_pushSubscriptions, body?.Endpoint);
            return Results.Json(result, Json, statusCode: result.Ok ? 200 : 400);
        });

        // 송출 미리보기 썸네일 (토큰은 쿼리스트링으로 — <img> 헤더 불가). 프레임 없으면 204.
        app.MapGet("/api/preview.jpg", () =>
        {
            var jpeg = _preview.GetLatestJpegFrame();
            return jpeg is null
                ? Results.StatusCode(StatusCodes.Status204NoContent)
                : Results.Bytes(jpeg, "image/jpeg");
        });
        // A saved inspection image is distinct from the live WebSocket preview: it carries a
        // download filename and remains available as a one-shot fallback for old clients.
        app.MapGet("/api/snapshot.jpg", () =>
        {
            var jpeg = _preview.GetLatestJpegFrame();
            return jpeg is null
                ? Results.StatusCode(StatusCodes.Status204NoContent)
                : Results.File(jpeg, "image/jpeg", $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
        });

        app.MapPost("/api/live/start", async () =>
        {
            await _orchestrator.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return Results.Json(new { ok = true, state = _orchestrator.State.ToString() }, Json);
        });
        app.MapPost("/api/live/stop", async () =>
        {
            await _orchestrator.StopAsync().ConfigureAwait(false);
            return Results.Json(new { ok = true, state = _orchestrator.State.ToString() }, Json);
        });
        // 방송만 중지(녹화 계속): 인코더가 녹화 전용으로 전환되고 브로드캐스트는 종료된다.
        // 워치독 재연결 중(ConnectingYouTube/Retrying)에는 no-op이므로 ok=false로 정직하게 알린다 —
        // 폰이 성공 토스트를 띄운 채 방송이 스스로 되살아나는 상황을 막는다.
        app.MapPost("/api/live/stop-broadcast", async () =>
        {
            await _orchestrator.StopStreamingKeepRecordingAsync().ConfigureAwait(false);
            var state = _orchestrator.State;
            var ok = state is StreamState.RecordingOnly or StreamState.Stopping or StreamState.Idle;
            return Results.Json(new { ok, state = state.ToString() }, Json);
        });
        app.MapPost("/api/live/retry", async () =>
        {
            var ok = await _orchestrator.ForceRetryAsync().ConfigureAwait(false);
            var state = _orchestrator.State;
            return Results.Json(new
            {
                ok,
                state = state.ToString(),
                message = ok ? "송출 복구를 요청했습니다." : "현재 상태에서는 송출 복구를 요청할 수 없습니다."
            }, Json, statusCode: ok ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/app/restart", () =>
        {
            var ok = _appRestart.RequestRestart();
            return Results.Json(new
            {
                ok,
                message = ok ? "앱을 안전하게 다시 시작합니다." : "앱 재시작이 이미 진행 중입니다."
            }, Json, statusCode: ok ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/system/shutdown", () =>
        {
            var ok = _systemShutdown.RequestShutdown();
            return Results.Json(new
            {
                ok,
                message = ok
                    ? "PC 종료를 준비합니다. 녹화와 방송을 안전하게 마친 뒤 전원을 끕니다."
                    : "PC 종료가 이미 준비 중입니다."
            }, Json, statusCode: ok ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/app/uninstall", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync<RemoteUninstallRequest>(ctx).ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.AdministratorToken))
            {
                return Results.Json(new { ok = false, message = "관리자 인증이 필요합니다." }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var result = await _appUninstall.RequestAsync(body.AdministratorToken, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = result.Ok, message = result.Message }, Json,
                statusCode: result.Ok ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        });

        app.Map("/ws/status", HandleWebSocketAsync);
    }

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var channel = new SocketChannel(socket);
        lock (_socketsGate)
        {
            _sockets.Add(channel);
        }
        await SendStatusAsync(channel, ctx.RequestAborted).ConfigureAwait(false);
        var preview = _preview.GetLatestJpegFrame();
        if (preview is not null)
        {
            await SendBinaryAsync(channel, preview, ctx.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ctx.RequestAborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            lock (_socketsGate)
            {
                _sockets.Remove(channel);
            }
        }
    }

    // ---- status push ----

    private void OnMetrics(object? sender, MetricsSnapshot metrics)
    {
        _lastMetrics = metrics;
        BroadcastStatus();
    }

    private void OnStateOrMetricsChanged(object? sender, StreamState state) => BroadcastStatus();

    private void OnQualityChanged(object? sender, QualityStatus quality) => BroadcastStatus();

    private void OnSplitsChanged(object? sender, EventArgs e) => BroadcastStatus();

    private void OnHealthChanged(object? sender, HealthEvent e) => BroadcastStatus();

    private void OnPreviewFrame() => BroadcastPreview();

    /// <summary>GET /api/splits body — also embedded in the status push as <c>splits</c>.</summary>
    private SplitRemote.SplitsDto BuildSplits()
    {
        var config = _configStore.Load();
        return SplitRemote.BuildDto(_splits, _scheduleStore, config.Periods, TitleTemplater.ResolveRoomName(config),
            DateTime.Now);
    }

    // ---- audio levels + mic-signal push ----

    private void OnLevels(object? sender, AudioLevels levels)
    {
        // Mixer emits ~16 Hz; halve it to ~8 Hz for the phone to keep the LAN/Tailscale link light.
        if (Interlocked.Increment(ref _levelTick) % 2 != 0)
        {
            return;
        }
        BroadcastLevels(levels);
    }

    private void OnMicSignal(object? sender, MicSignalStatus status)
    {
        var name = _audioMixer.Sources.FirstOrDefault(s => s.Id == status.SourceId)?.Name ?? "마이크";
        lock (_silentMicsGate)
        {
            if (status.SignalPresent)
            {
                _silentMics.Remove(status.SourceId);
            }
            else
            {
                _silentMics[status.SourceId] = name;
            }
        }
        BroadcastStatus(); // refresh the warning banner promptly
    }

    private void BroadcastPreview()
    {
        var jpeg = _preview.GetLatestJpegFrame();
        if (jpeg is null)
        {
            return;
        }

        List<SocketChannel> channels;
        lock (_socketsGate)
        {
            if (_sockets.Count == 0)
            {
                return;
            }
            channels = _sockets.ToList();
        }

        foreach (var channel in channels)
        {
            // Preview is a newest-frame-wins surface. A slow phone must never queue a growing
            // chain of JPEGs behind status messages; drop frames while its previous one is sending.
            if (Interlocked.Exchange(ref channel.PreviewSendInFlight, 1) == 0)
            {
                _ = SendPreviewAsync(channel, jpeg);
            }
        }
    }

    private async Task SendPreviewAsync(SocketChannel channel, byte[] jpeg)
    {
        try
        {
            await SendBinaryAsync(channel, jpeg).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref channel.PreviewSendInFlight, 0);
        }
    }

    private void BroadcastLevels(AudioLevels levels)
    {
        List<SocketChannel> channels;
        lock (_socketsGate)
        {
            if (_sockets.Count == 0)
            {
                return;
            }
            channels = _sockets.ToList();
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(BuildLevels(levels), Json);
        foreach (var channel in channels)
        {
            _ = SendRawAsync(channel, payload);
        }
    }

    private object BuildLevels(AudioLevels levels)
    {
        var names = _audioMixer.Sources.ToDictionary(s => s.Id, s => s.Name);
        return new
        {
            type = "levels",
            master = new { rms = ToFraction(levels.MasterRmsDb), peak = ToFraction(levels.MasterPeakDb) },
            sources = levels.Sources.Select(l => new
            {
                id = l.Id,
                name = names.TryGetValue(l.Id, out var n) ? n : l.Id,
                rms = ToFraction(l.RmsDb),
                peak = ToFraction(l.PeakDb)
            })
        };
    }

    private object BuildAudio()
    {
        var levels = _audioMixer.CurrentLevels.Sources.ToDictionary(l => l.Id);
        string[] silent;
        lock (_silentMicsGate)
        {
            silent = _silentMics.Values.ToArray();
        }
        return new
        {
            micWarning = silent.Length > 0,
            silent,
            sources = _audioMixer.Sources.Select(s =>
            {
                levels.TryGetValue(s.Id, out var l);
                return new
                {
                    id = s.Id,
                    name = s.Name,
                    kind = s.Kind == AudioSourceKind.System ? "system" : "mic",
                    gain = s.Gain,
                    muted = s.Muted,
                    gate = s.GateEnabled,
                    rms = l is null ? 0 : ToFraction(l.RmsDb),
                    peak = l is null ? 0 : ToFraction(l.PeakDb)
                };
            })
        };
    }

    private void PersistSourceChange(string id, Action<AudioSourceConfig> mutate) =>
        _configStore.Update(config =>
        {
            var source = config.Audio.Sources.FirstOrDefault(
                s => AudioConfigMapper.SourceId(s.Kind, s.DeviceId) == id);
            if (source is not null)
            {
                mutate(source);
            }
        });

    private static double ToFraction(double db)
    {
        const double floor = -60;
        return db <= floor ? 0 : Math.Clamp((db - floor) / -floor, 0, 1);
    }

    private void BroadcastStatus()
    {
        List<SocketChannel> channels;
        lock (_socketsGate)
        {
            if (_sockets.Count == 0)
            {
                return;
            }
            channels = _sockets.ToList();
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(BuildStatus(), Json);
        foreach (var channel in channels)
        {
            _ = SendRawAsync(channel, payload);
        }
    }

    private async Task SendStatusAsync(SocketChannel channel, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(BuildStatus(), Json);
        await SendRawAsync(channel, payload, ct).ConfigureAwait(false);
    }

    private Task SendRawAsync(SocketChannel channel, byte[] payload, CancellationToken ct = default) =>
        SendAsync(channel, payload, WebSocketMessageType.Text, ct);

    private Task SendBinaryAsync(SocketChannel channel, byte[] payload, CancellationToken ct = default) =>
        SendAsync(channel, payload, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(
        SocketChannel channel,
        byte[] payload,
        WebSocketMessageType messageType,
        CancellationToken ct = default)
    {
        // Serialize writes per socket: concurrent SendAsync on one WebSocket (status + levels
        // frames from different producer threads) throws InvalidOperationException and aborts it.
        try
        {
            await channel.SendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (channel.Socket.State == WebSocketState.Open)
            {
                await channel.Socket.SendAsync(payload, messageType, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            lock (_socketsGate)
            {
                _sockets.Remove(channel);
            }
        }
        finally
        {
            channel.SendLock.Release();
        }
    }

    // ---- period download assets ----

    private object BuildPeriodAssets() => new
    {
        items = _periodAssets.Snapshot().Take(200).Select(asset => new
        {
            id = asset.Id,
            date = asset.Date.ToString("yyyy-MM-dd"),
            period = asset.PeriodNumber,
            title = asset.Title,
            videoId = asset.VideoId,
            audioAvailable = GetSafeAssetAudioPath(asset.AudioPath) is not null,
            captionStatus = asset.CaptionStatus,
            captionLanguage = asset.CaptionLanguage,
            captionMessage = asset.CaptionMessage
        })
    };

    private void UpdateCaptionStatusSafely(string id, string status, string? language = null, string? message = null)
    {
        try
        {
            _periodAssets.MarkCaptionStatus(id, status, language, message);
        }
        catch (Exception ex)
        {
            // A transient catalogue issue must not prevent an otherwise successful file response.
            _log.Warn($"교시 자막 상태 저장 실패: {ex.Message}");
        }
    }

    private static string? GetSafeAssetAudioPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.PeriodAudioDir)) +
                       Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(candidate);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetExtension(fullPath), ".m4a", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static string PeriodAssetDownloadName(PeriodAsset asset, string extension)
    {
        var fallback = $"{asset.Date:yyyy-MM-dd}_{asset.PeriodNumber}교시";
        var stem = string.IsNullOrWhiteSpace(asset.Title) ? fallback : asset.Title.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(stem.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? fallback + "." + extension : safe + "." + extension;
    }

    // ---- status DTO ----

    private RemoteDiagnosticsDto BuildDiagnostics(DateTime nowLocal) =>
        RemoteDiagnostics.Build(
            nowLocal,
            _scheduleStore.ResolveForDate(DateOnly.FromDateTime(nowLocal)),
            _health.ActiveEvents,
            InMemoryLogSink.Snapshot());

    private object BuildAutomation() => new
    {
        enabled = _configStore.Load().AutomaticOperationEnabled
    };

    private object BuildSettingsLock()
    {
        var mode = RemoteSettingsLock.Normalize(_configStore.Load().Remote.SettingsLockMode);
        return new
        {
            mode,
            scheduleEditable = RemoteSettingsLock.Allows(mode, RemoteSettingsSection.Schedule),
            otherSettingsEditable = RemoteSettingsLock.Allows(mode, RemoteSettingsSection.Other)
        };
    }

    private IResult? BlockSettingsChange(RemoteSettingsSection section)
    {
        var mode = RemoteSettingsLock.Normalize(_configStore.Load().Remote.SettingsLockMode);
        if (RemoteSettingsLock.Allows(mode, section))
        {
            return null;
        }

        return Results.Json(new
        {
            ok = false,
            error = RemoteSettingsLock.DenialMessage(section),
            settingsLock = BuildSettingsLock()
        }, Json, statusCode: StatusCodes.Status423Locked);
    }

    private object BuildStatus()
    {
        var state = _orchestrator.State;
        var recording = _recording.GetStatus();
        var jobs = _uploadQueue.Snapshot();
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var quality = _orchestrator.CurrentQuality;
        string[] silentMics;
        lock (_silentMicsGate)
        {
            silentMics = _silentMics.Values.ToArray();
        }

        return new
        {
            state = state.ToString(),
            badge = StateBadge(state),
            live = state == StreamState.Live,
            room = _roomName, // 호실명 — shown in the phone header so a manager knows which PC this is
            recovery = _recovery.Status,
            automation = BuildAutomation(),
            settingsLock = BuildSettingsLock(),

            // 적용 중인 송출 품질(적응형 품질) — 폰 상태 카드의 품질 줄 + 프리셋 하이라이트용.
            quality = new
            {
                mode = quality.Mode == QualityMode.ManualHold ? "manual" : "auto",
                level = quality.Level,
                levelName = quality.LevelName,
                degraded = quality.Level > 0,
                current = quality.Applied is { } step
                    ? (object)new
                    {
                        width = step.Width, height = step.Height,
                        fps = step.Fps, videoBitrateKbps = step.VideoBitrateKbps
                    }
                    : null
            },

            audio = new
            {
                micWarning = silentMics.Length > 0,
                silent = silentMics
            },
            metrics = new
            {
                bitrateKbps = _lastMetrics.UploadBitrateKbps,
                fps = _lastMetrics.Fps,
                cpu = _lastMetrics.CpuPercent,
                gpu = _lastMetrics.GpuPercent
            },
            recording = new
            {
                file = recording.CurrentFilePath is null ? null : Path.GetFileName(recording.CurrentFilePath),
                usedBytes = recording.TotalUsedBytes,
                freeBytes = recording.FreeDiskBytes
            },
            today = new
            {
                date = today.ToString("yyyy-MM-dd"),
                hasOverride = _scheduleStore.GetOverride(today) is not null,
                periods = ToDtos(_scheduleStore.ResolveForDate(today))
            },
            diagnostics = BuildDiagnostics(now),
            // 승인 기반 분할(v8): 승인 카드 + 연강 체인 + 최근 이력. 구버전 페이지는 이 필드를
            // 모른 채 무시하고, 새 페이지는 null-guard로 구서버와도 호환된다.
            splits = BuildSplits(),
            queue = new
            {
                pending = jobs.Count(j => j.Status == UploadJobStatus.Pending),
                uploading = jobs.Count(j => j.Status == UploadJobStatus.Uploading),
                completed = jobs.Count(j => j.Status == UploadJobStatus.Completed),
                failed = jobs.Count(j => j.Status == UploadJobStatus.Failed),
                items = jobs.TakeLast(20).Select(j => new
                {
                    period = j.PeriodNumber,
                    title = j.Title,
                    status = j.Status,
                    videoId = j.VideoId
                })
            }
        };
    }

    /// <summary>
    /// Lightweight per-room snapshot for the phone's multi-room grid (Phase 2). One card polls this
    /// every 5–10 s per room, so it stays small: state/badge, mic silence, bitrate, free disk, and
    /// the health monitor's active conditions — no timetable, no upload-queue listing.
    /// </summary>
    private object BuildSummary()
    {
        var state = _orchestrator.State;
        var recording = _recording.GetStatus();
        string[] silentMics;
        lock (_silentMicsGate)
        {
            silentMics = _silentMics.Values.ToArray();
        }

        var quality = _orchestrator.CurrentQuality;
        return new
        {
            room = _roomName,
            state = state.ToString(),
            badge = StateBadge(state),
            live = state == StreamState.Live,
            micWarning = silentMics.Length > 0,
            silent = silentMics,
            bitrateKbps = _lastMetrics.UploadBitrateKbps,
            freeBytes = recording.FreeDiskBytes,
            qualityLevel = quality.Level,   // 그리드 카드의 "절약" 칩용 (0 = 원본)
            qualityName = quality.LevelName,
            // 그리드 "승인 대기 N" 칩 + 일괄 승인 대상 판별용
            pendingSplits = _splits.Snapshot().Count(s => s.Status == PendingSplitStatus.Pending),
            issues = _health.ActiveEvents.Select(e => new
            {
                kind = e.Kind.ToString(),
                severity = e.Severity.ToString(),
                message = e.Message
            })
        };
    }

    private static string StateBadge(StreamState state) => state switch
    {
        StreamState.Live => "LIVE",
        StreamState.Warmup => "준비 중",
        StreamState.ConnectingYouTube => "연결 중",
        StreamState.Retrying => "재시도 중",
        StreamState.Stopping => "중지 중",
        StreamState.RecordingOnly => "녹화만",
        _ => "대기"
    };

    private Dictionary<string, List<PeriodDto>> GetWeekdayDefaults()
    {
        var result = new Dictionary<string, List<PeriodDto>>();
        foreach (var (key, day) in WeekdayMap)
        {
            result[key] = ToDtos(_scheduleStore.GetWeekdayDefault(day));
        }
        return result;
    }

    private static List<PeriodDto> ToDtos(DaySchedule schedule) =>
        schedule.Periods
            .Select(p => new PeriodDto(p.Number,
                p.Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                p.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))
            .ToList();

    private static DaySchedule ToDaySchedule(IEnumerable<PeriodDto> rows) =>
        new(rows
            .Where(r => TimeOnly.TryParse(r.Start, out _) && TimeOnly.TryParse(r.End, out _))
            .Select(r => new SchoolPeriod(r.No, TimeOnly.Parse(r.Start), TimeOnly.Parse(r.End)))
            .OrderBy(p => p.Number)
            .ToList());

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx)
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(Json).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // ---- network / firewall ----

    private string? ResolveBindAddress(RemoteBindMode mode) => mode switch
    {
        RemoteBindMode.Tailscale => FindTailscaleIp(),
        // Cloudflare exposes the loopback server through the tunnel — never bind it to the LAN.
        RemoteBindMode.Cloudflare => "127.0.0.1",
        _ => "0.0.0.0"
    };

    private static string? FindTailscaleIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var isTailscale = nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                              nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                // Tailscale uses the 100.64.0.0/10 CGNAT range.
                if (isTailscale || IsCgnat(ua.Address))
                {
                    return ua.Address.ToString();
                }
            }
        }
        return null;
    }

    private static bool IsCgnat(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static string? FirstLanIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IsCgnat(ua.Address))
                {
                    return ua.Address.ToString();
                }
            }
        }
        return null;
    }

    private void TryConfigureFirewall(int port)
    {
        var ruleName = $"MediaCaptureHelper Remote {port}";
        try
        {
            // Replace any stale rule, then add a fresh inbound TCP allow (best-effort; needs admin).
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            var exit = RunNetsh(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");
            if (exit == 0)
            {
                _firewallPort = port; // remember it so StopAsync can remove it (B4: no leaked rule)
                _log.Info($"Windows 방화벽 인바운드 규칙을 추가했습니다(TCP {port}).");
            }
            else
            {
                _log.Info($"방화벽 규칙 추가 실패(관리자 권한 필요할 수 있음). 수동 허용: TCP {port}.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"방화벽 규칙 설정 실패(수동으로 TCP {port} 인바운드 허용 필요): {ex.Message}");
        }
    }

    private static int RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
        {
            return -1;
        }
        process.WaitForExit(10_000);
        return process.HasExited ? process.ExitCode : -1;
    }

    private static string LoadEmbeddedHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return "<html><body><h1>Media Capture Helper 원격</h1><p>UI 리소스를 찾지 못했습니다.</p></body></html>";
        }
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Reads an embedded text asset by filename suffix (PWA manifest/service worker/SVG).</summary>
    private static string LoadEmbeddedText(string suffix)
    {
        using var stream = OpenEmbedded(suffix);
        if (stream is null)
        {
            return string.Empty;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Reads an embedded binary asset by filename suffix (PWA icons).</summary>
    private static byte[] LoadEmbeddedBytes(string suffix)
    {
        using var stream = OpenEmbedded(suffix);
        if (stream is null)
        {
            return [];
        }
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream? OpenEmbedded(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }

    private sealed record PairRequest(string? Pin);

    private sealed record SettingsLockRequest(string? Mode);

    private sealed record AutomationRequest(bool? Enabled);

    private sealed record PeriodDto(int No, string Start, string End);

    private sealed record AudioMuteRequest(string? Id, bool Muted);

    private sealed record AudioGainRequest(string? Id, double Gain);

    private sealed record RemoteUninstallRequest(string? AdministratorToken);

    /// <summary>A connected WebSocket plus a 1-permit gate that serialises sends to it.</summary>
    private sealed class SocketChannel(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public int PreviewSendInFlight;
    }
}
