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
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;

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
    private readonly IUploadQueue _uploadQueue;
    private readonly IRecordingManager _recording;
    private readonly IAudioMixer _audioMixer;
    private readonly IPreviewProvider _preview;
    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _tokenProtector;
    private readonly ILogService _log;

    private readonly object _socketsGate = new();
    private readonly List<SocketChannel> _sockets = [];
    private readonly object _silentMicsGate = new();
    private readonly Dictionary<string, string> _silentMics = new();
    private readonly string _html;

    private readonly CloudflaredManager _cloudflared;
    private readonly PairingThrottle _pairThrottle = new();
    private WebApplication? _app;
    private int? _firewallPort; // the port a runtime firewall rule was opened for (LAN mode), to remove on stop
    private MetricsSnapshot _lastMetrics = MetricsSnapshot.Empty;
    private int _levelTick;

    public RemoteControlServer(
        IStreamOrchestrator orchestrator,
        IPeriodScheduleStore scheduleStore,
        PeriodScheduler scheduler,
        IUploadQueue uploadQueue,
        IRecordingManager recording,
        IAudioMixer audioMixer,
        IPreviewProvider preview,
        IConfigStore configStore,
        ITokenProtector tokenProtector,
        ILogService log)
    {
        _orchestrator = orchestrator;
        _scheduleStore = scheduleStore;
        _scheduler = scheduler;
        _uploadQueue = uploadQueue;
        _recording = recording;
        _audioMixer = audioMixer;
        _preview = preview;
        _configStore = configStore;
        _tokenProtector = tokenProtector;
        _log = log;
        _cloudflared = new CloudflaredManager(log);
        _html = LoadEmbeddedHtml();
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

        var url = $"http://{bindIp}:{port}";
        var displayHost = mode == RemoteBindMode.Lan ? FirstLanIpv4() ?? bindIp : bindIp;
        _log.Info($"원격 제어 서버 시작: {url}  —  폰 브라우저로 http://{displayHost}:{port} 접속 후 PIN [{CurrentPin}] 입력.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();

        app.UseWebSockets();
        ConfigureAuth(app);
        MapEndpoints(app);

        _orchestrator.StateChanged += OnStateOrMetricsChanged;
        _orchestrator.MetricsUpdated += OnMetrics;
        _audioMixer.LevelsUpdated += OnLevels;
        _audioMixer.MicSignalChanged += OnMicSignal;

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
        try
        {
            var publicUrl = await _cloudflared.StartAsync(token, port, hostname, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(publicUrl))
            {
                CurrentPublicUrl = publicUrl;
                PublicUrlChanged?.Invoke(publicUrl);
                _log.Info($"Cloudflare 터널 공개 주소: {publicUrl}  —  폰 브라우저로 접속 후 PIN [{CurrentPin}] 입력.");
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
        _orchestrator.StateChanged -= OnStateOrMetricsChanged;
        _orchestrator.MetricsUpdated -= OnMetrics;
        _audioMixer.LevelsUpdated -= OnLevels;
        _audioMixer.MicSignalChanged -= OnMicSignal;

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
            RunNetsh($"advfirewall firewall delete rule name=\"SilentStream Remote {port}\"");
            _log.Info($"원격 방화벽 인바운드 규칙을 제거했습니다(TCP {port}).");
        }
        catch (Exception ex)
        {
            _log.Debug($"방화벽 규칙 제거 실패(다음 시작 시 재정리됨): {ex.Message}");
        }
    }

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

        app.MapGet("/api/schedule", () => Results.Json(GetWeekdayDefaults(), Json));
        app.MapPut("/api/schedule", async ctx =>
        {
            var body = await ReadJsonAsync<Dictionary<string, List<PeriodDto>>>(ctx).ConfigureAwait(false);
            if (body is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
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
            await ctx.Response.WriteAsJsonAsync(new { ok = true }).ConfigureAwait(false);
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
        app.MapPut("/api/schedule/today", async ctx =>
        {
            var rows = await ReadJsonAsync<List<PeriodDto>>(ctx).ConfigureAwait(false) ?? [];
            var today = DateOnly.FromDateTime(DateTime.Now);
            _scheduleStore.SetOverride(today, ToDaySchedule(rows));
            _scheduler.NotifyScheduleChanged();
            _log.Info($"오늘({today:yyyy-MM-dd}) 시간표 덮어쓰기가 적용되었습니다(원격).");
            await ctx.Response.WriteAsJsonAsync(new { ok = true }).ConfigureAwait(false);
        });
        app.MapDelete("/api/schedule/today", () =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            _scheduleStore.ClearOverride(today);
            _scheduler.NotifyScheduleChanged();
            _log.Info($"오늘({today:yyyy-MM-dd}) 시간표 덮어쓰기를 해제했습니다(원격).");
            return Results.Json(new { ok = true }, Json);
        });

        // ---- audio mixer (다중 채널 + 증폭 + 실시간 미터) ----
        app.MapGet("/api/audio", () => Results.Json(BuildAudio(), Json));
        app.MapPost("/api/audio/mute", async ctx =>
        {
            var body = await ReadJsonAsync<AudioMuteRequest>(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body?.Id))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            _audioMixer.SetMuted(body.Id, body.Muted);
            PersistSourceChange(body.Id, s => s.Muted = body.Muted);
            await ctx.Response.WriteAsJsonAsync(new { ok = true }).ConfigureAwait(false);
        });
        app.MapPost("/api/audio/gain", async ctx =>
        {
            var body = await ReadJsonAsync<AudioGainRequest>(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body?.Id))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var gain = Math.Clamp(body.Gain, 0, 4);
            _audioMixer.SetGain(body.Id, gain);
            PersistSourceChange(body.Id, s => s.Gain = gain);
            await ctx.Response.WriteAsJsonAsync(new { ok = true }).ConfigureAwait(false);
        });

        // 송출 미리보기 썸네일 (토큰은 쿼리스트링으로 — <img> 헤더 불가). 프레임 없으면 204.
        app.MapGet("/api/preview.jpg", () =>
        {
            var jpeg = _preview.GetLatestJpegFrame();
            return jpeg is null
                ? Results.StatusCode(StatusCodes.Status204NoContent)
                : Results.Bytes(jpeg, "image/jpeg");
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

    private async Task SendRawAsync(SocketChannel channel, byte[] payload, CancellationToken ct = default)
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
                await channel.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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

    // ---- status DTO ----

    private object BuildStatus()
    {
        var state = _orchestrator.State;
        var recording = _recording.GetStatus();
        var jobs = _uploadQueue.Snapshot();
        var today = DateOnly.FromDateTime(DateTime.Now);
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

    private static string StateBadge(StreamState state) => state switch
    {
        StreamState.Live => "LIVE",
        StreamState.Warmup => "준비 중",
        StreamState.ConnectingYouTube => "연결 중",
        StreamState.Retrying => "재시도 중",
        StreamState.Stopping => "중지 중",
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
        var ruleName = $"SilentStream Remote {port}";
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
            return "<html><body><h1>SilentStream 원격</h1><p>UI 리소스를 찾지 못했습니다.</p></body></html>";
        }
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record PairRequest(string? Pin);

    private sealed record PeriodDto(int No, string Start, string End);

    private sealed record AudioMuteRequest(string? Id, bool Muted);

    private sealed record AudioGainRequest(string? Id, double Gain);

    /// <summary>A connected WebSocket plus a 1-permit gate that serialises sends to it.</summary>
    private sealed class SocketChannel(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
