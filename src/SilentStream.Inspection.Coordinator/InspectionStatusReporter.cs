using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

/// <summary>
/// Mirrors the Coordinator's durable SQLite result into the existing authenticated app remote so
/// the M111/M112/M113/백202 grid and per-room inspection card show the same run after refresh/reboot.
/// </summary>
public sealed class InspectionStatusReporter : BackgroundService
{
    private readonly InspectionStore _store;
    private readonly CoordinatorOptionsStore _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly StartupUpdateGate _startup;
    private readonly ILogger<InspectionStatusReporter> _logger;
    private string? _lastDeliveredFingerprint;

    public InspectionStatusReporter(InspectionStore store, CoordinatorOptionsStore options,
        IHttpClientFactory httpFactory, StartupUpdateGate startup, ILogger<InspectionStatusReporter> logger)
    {
        _store = store;
        _options = options;
        _httpFactory = httpFactory;
        _startup = startup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runId = _store.GetLatestRunId();
                var progress = runId is null ? null : _store.GetRunProgress(runId);
                if (progress is not null) await DeliverAsync(progress, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "Inspection status could not be mirrored to the app yet."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync(InspectionRunProgress progress, CancellationToken token)
    {
        var phase = progress.Status switch
        {
            InspectionRunStatus.Pass => "completed",
            InspectionRunStatus.Warning => "completed-with-warning",
            InspectionRunStatus.Fail or InspectionRunStatus.Unknown or InspectionRunStatus.Cancelled => "failed",
            _ => "running"
        };
        var severity = progress.Status switch
        {
            InspectionRunStatus.Pass => "success",
            InspectionRunStatus.Warning => "warning",
            InspectionRunStatus.Fail or InspectionRunStatus.Unknown or InspectionRunStatus.Cancelled => "error",
            _ => "info"
        };
        var percent = progress.TotalSteps == 0 ? 0
            : (int)Math.Round(progress.TerminalSteps * 100d / progress.TotalSteps);
        var message = progress.Status switch
        {
            InspectionRunStatus.Pass => "자동 장치 경로 점검을 통과했습니다. 실제 음질은 최종 청취로 확인하세요.",
            InspectionRunStatus.Warning => "자동 점검만으로 준비 완료를 확정하지 못했습니다. 표시 항목을 확인하고 재점검하세요.",
            InspectionRunStatus.Fail => "필수 장치 경로에 문제가 있습니다. 조치 후 다시 점검하세요.",
            InspectionRunStatus.Unknown => "점검 판정을 확정할 수 없습니다. 상세 결과를 확인한 뒤 다시 점검하세요.",
            InspectionRunStatus.Cancelled => "실기기 점검이 취소되었습니다.",
            _ => progress.CurrentMessage ?? "실기기 점검을 진행하고 있습니다."
        };

        // The Coordinator's own startup state is authoritative and must not depend on the app
        // being online. Reconcile it before HTTP delivery and before fingerprint de-duplication so
        // a service restart cannot leave a terminal run displayed as queued forever.
        var startup = _startup.Snapshot;
        if (startup.RunId == progress.RunId && InspectionStore.IsTerminal(progress.Status) &&
            (!string.Equals(startup.Phase, phase, StringComparison.Ordinal) ||
             !string.Equals(startup.Message, message, StringComparison.Ordinal)))
        {
            _startup.MarkInspection(phase, message, progress.RunId);
        }

        var fingerprint = $"{progress.RunId}|{progress.Status}|{progress.TerminalSteps}|{progress.CurrentStep}|{string.Join('|', progress.Issues)}";
        if (fingerprint == _lastDeliveredFingerprint) return;

        var payload = new
        {
            runId = progress.RunId,
            room = _options.Current.Room,
            phase,
            progressPercent = phase is "completed" or "completed-with-warning" or "failed" ? 100 : percent,
            currentStep = progress.CurrentStep ?? string.Empty,
            message,
            severity,
            resultsPath = $"/inspect/?run={progress.RunId}",
            recentLog = progress.Issues,
            issues = progress.Issues,
            rerunCount = 0
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8787/api/inspection/report")
        {
            Content = JsonContent.Create(payload, options: InspectionProtocol.Json)
        };
        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
            request.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", appToken);
        }
        using var response = await _httpFactory.CreateClient().SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _lastDeliveredFingerprint = fingerprint;
    }
}
