using Microsoft.Extensions.Hosting;
using System.Management;

namespace SilentStream.Inspection.Coordinator;

public sealed record StartupReadiness(bool UpdateCheckSucceeded, string Message);

public sealed class StartupUpdateGate
{
    private readonly TaskCompletionSource<StartupReadiness> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private StartupAutomationSnapshot _snapshot = new("checking-update", "업데이트를 확인하고 있습니다.", null, DateTimeOffset.UtcNow);

    public Task<StartupReadiness> WaitAsync(CancellationToken token) => _ready.Task.WaitAsync(token);

    public StartupAutomationSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void MarkApplying(string message)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted) return;
            _snapshot = new("applying-update", message, null, DateTimeOffset.UtcNow);
        }
    }

    public void MarkReady(bool succeeded, string message)
    {
        lock (_gate)
        {
            if (_ready.Task.IsCompleted) return;
            _snapshot = new(succeeded ? "update-ready" : "update-warning", message, null, DateTimeOffset.UtcNow);
            _ready.TrySetResult(new StartupReadiness(succeeded, message));
        }
    }

    public void MarkInspection(string phase, string message, string? runId = null)
    {
        lock (_gate) _snapshot = new(phase, message, runId, DateTimeOffset.UtcNow);
    }
}

public sealed record StartupAutomationSnapshot(string Phase, string Message, string? RunId, DateTimeOffset UpdatedAtUtc);

public static class SystemBootSession
{
    public static string CreateId(DateTimeOffset utcNow, long uptimeMilliseconds)
    {
        var boot = utcNow - TimeSpan.FromMilliseconds(Math.Max(0, uptimeMilliseconds));
        // The native uptime clock and wall clock are sampled a few instructions apart. Round to
        // the nearest 30-second boundary so tiny sampling jitter on either side of a boundary does
        // not turn one Windows boot into two automatic inspection runs.
        return Math.Round(boot.ToUnixTimeMilliseconds() / 30_000d, MidpointRounding.AwayFromZero)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string CurrentId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            var raw = results.Cast<ManagementObject>().Select(item => item["LastBootUpTime"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var bootUtc = ManagementDateTimeConverter.ToDateTime(raw).ToUniversalTime();
                return $"wmi:{bootUtc:O}";
            }
        }
        catch (ManagementException)
        {
            // WMI can be temporarily unavailable early in boot; the uptime fallback is still
            // stable in ordinary service-restart sampling and startup_runs remains durable.
        }

        return $"uptime:{CreateId(DateTimeOffset.UtcNow, Environment.TickCount64)}";
    }
}

public sealed class StartupInspectionHostedService : BackgroundService
{
    private readonly CoordinatorOptionsStore _options;
    private readonly InspectionStore _store;
    private readonly StartupUpdateGate _startup;
    private readonly ILogger<StartupInspectionHostedService> _logger;
    private readonly IHttpClientFactory? _httpFactory;

    public StartupInspectionHostedService(CoordinatorOptionsStore options, InspectionStore store,
        StartupUpdateGate startup, ILogger<StartupInspectionHostedService> logger, IHttpClientFactory? httpFactory = null)
    {
        _options = options;
        _store = store;
        _startup = startup;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var readiness = await _startup.WaitAsync(stoppingToken).ConfigureAwait(false);
        var current = _options.Current;
        if (!current.StartupInspectionEnabled)
        {
            _startup.MarkInspection("disabled", "부팅 자동 점검이 설정에서 꺼져 있습니다.");
            return;
        }

        var roomId = NormalizeRoomId(string.IsNullOrWhiteSpace(current.RoomId) ? current.Room : current.RoomId);
        var managed = current.ManagedRooms.Select(NormalizeRoomId).Contains(roomId, StringComparer.OrdinalIgnoreCase);
        if (!managed)
        {
            _startup.MarkInspection("excluded", $"{current.Room}은(는) 현재 관리 대상 호실이 아닙니다.");
            return;
        }

        if (!await WaitForInteractiveAppAsync(current, stoppingToken).ConfigureAwait(false)) return;

        var delay = TimeSpan.FromSeconds(Math.Clamp(current.StartupInspectionDelaySeconds, 0, 300));
        if (delay > TimeSpan.Zero)
        {
            _startup.MarkInspection("waiting-for-app", $"{current.Room} 앱과 로그인 세션을 기다리고 있습니다.");
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }

        var candidates = _store.ListStoredBundles()
            .Where(bundle => bundle.Manifest.Plan.RunAtStartup)
            .OrderByDescending(bundle => ParseVersion(bundle.Manifest.Version))
            .ToArray();
        if (candidates.Length == 0)
        {
            _startup.MarkInspection("not-configured", "서명된 부팅 안전 점검 패키지가 없습니다.");
            _store.Audit("system", "startup.run", "skipped", $"{current.Room}:bundle-missing");
            return;
        }

        var bundle = candidates[0];
        if (!InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason))
        {
            _startup.MarkInspection("blocked", $"부팅 점검 패키지가 안전 정책을 통과하지 못했습니다: {reason}");
            _store.Audit("system", "startup.run", "denied", reason);
            return;
        }

        var bootId = SystemBootSession.CurrentId();
        bool created;
        string runId;
        while (true)
        {
            created = _store.TryCreateStartupRun(bundle, bootId, out runId);
            if (!string.IsNullOrWhiteSpace(runId)) break;
            _startup.MarkInspection("waiting-for-update", "점검 서비스 업데이트 예약이 끝나기를 기다리고 있습니다.");
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        _startup.MarkInspection(created ? "inspection-queued" : "inspection-resumed",
            readiness.UpdateCheckSucceeded
                ? $"{current.Room} 부팅 점검을 {(created ? "시작" : "이어갑니다")}."
                : $"업데이트 확인 주의 상태로 {current.Room} 부팅 점검을 {(created ? "시작" : "이어갑니다")}.", runId);
        if (created) _store.Audit("system", "startup.run", "queued", $"{current.Room}:{runId}:{bundle.Manifest.Version}");
        _logger.LogInformation("Startup inspection {RunId} selected for {Room} ({Created}).", runId, current.Room, created);
    }

    private async Task<bool> WaitForInteractiveAppAsync(CoordinatorOptions current, CancellationToken token)
    {
        if (_httpFactory is null) return true; // deterministic unit tests can exercise queueing without a desktop session

        var appToken = _options.UnprotectMachine(current.AppApiTokenMachineEnc);
        if (string.IsNullOrWhiteSpace(appToken))
        {
            _startup.MarkInspection("blocked", "앱 점검 연결 정보가 없어 자동 점검을 시작할 수 없습니다.");
            return false;
        }

        _startup.MarkInspection("login-pending", $"{current.Room} Windows 로그인과 앱 업데이트 완료를 기다리고 있습니다.");
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8787/api/status");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await _httpFactory.CreateClient().SendAsync(request, attempt.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    _startup.MarkInspection("blocked", "앱 점검 연결 권한을 다시 등록해야 합니다.");
                    return false;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Waiting for the interactive classroom app before startup inspection.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        return false;
    }

    internal static string NormalizeRoomId(string value)
    {
        var normalized = value.Trim().Replace("강의실", string.Empty, StringComparison.Ordinal)
            .Replace("호", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Length > 1 && normalized[0] == '백' && normalized[1..].All(char.IsDigit)
            ? $"baek{normalized[1..]}"
            : normalized;
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);
}
