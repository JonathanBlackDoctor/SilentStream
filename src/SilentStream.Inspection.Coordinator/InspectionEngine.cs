using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class InspectionEngine : BackgroundService
{
    private static readonly Regex TemplateToken = new(@"\{\{(?<token>[^{}]+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BrokerActions = new(StringComparer.Ordinal)
    {
        "restart-app", "kill-process", "reboot", "nic-bounce", "install-app", "uninstall-app", "restart-cloudflared"
    };
    private static readonly HashSet<string> JustInTimeApprovalActions = new(StringComparer.Ordinal)
    {
        "uninstall-app", "nic-bounce", "reboot"
    };
    private readonly InspectionStore _store;
    private readonly CoordinatorOptionsStore _options;
    private readonly ISessionAgentClient _session;
    private readonly BrokerClient _broker;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<InspectionEngine> _logger;

    public InspectionEngine(InspectionStore store, CoordinatorOptionsStore options, ISessionAgentClient session,
        BrokerClient broker, IHttpClientFactory httpFactory, ILogger<InspectionEngine> logger)
    {
        _store = store; _options = options; _session = session; _broker = broker; _httpFactory = httpFactory; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runs = _store.GetExecutableRuns();
                if (runs.Count == 0)
                {
                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var runId in runs) await ExecuteRunAsync(runId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inspection engine loop failed.");
                await Task.Delay(2000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteRunAsync(string runId, CancellationToken serviceToken)
    {
        var run = _store.GetStoredRun(runId);
        if (run is null) return;
        var bundle = _store.GetBundle(run.BundleId);
        if (bundle is null) { _store.UpdateRun(runId, InspectionRunStatus.Fail, "Bundle disappeared."); return; }
        _store.UpdateRun(runId, InspectionRunStatus.Running, "Inspection started.");

        while (true)
        {
            run = _store.GetStoredRun(runId);
            if (run is null) return;
            if (run.CancelRequested)
            {
                var cancellationStates = _store.GetSteps(runId);
                foreach (var pending in bundle.Manifest.Plan.Steps.Where(candidate => candidate.Type != InspectionStepType.Cleanup
                    && !InspectionStore.IsTerminal(cancellationStates[candidate.Id].Status)))
                {
                    var pendingState = cancellationStates[pending.Id];
                    _store.UpdateStep(runId, pending.Id, InspectionRunStatus.Cancelled, pendingState.Attempt,
                        "Cancelled by operator before cleanup.");
                }
                _store.UpdateRun(runId, InspectionRunStatus.Running, "Cancellation requested; running signed cleanup steps.");
            }
            if (run.PauseRequested && !run.CancelRequested)
            {
                _store.UpdateRun(runId, InspectionRunStatus.Paused, "Paused by operator.");
                await Task.Delay(1000, serviceToken).ConfigureAwait(false);
                continue;
            }
            if (run.Status == InspectionRunStatus.Paused) _store.UpdateRun(runId, InspectionRunStatus.Running, "Resumed.");

            var states = _store.GetSteps(runId);
            if (states.Values.All(s => InspectionStore.IsTerminal(s.Status))) break;
            var progressed = false;
            foreach (var step in bundle.Manifest.Plan.Steps)
            {
                var state = states[step.Id];
                if (InspectionStore.IsTerminal(state.Status)) continue;
                if (state.Status == InspectionRunStatus.Running)
                {
                    _store.UpdateStep(runId, step.Id, InspectionRunStatus.Unknown, state.Attempt, "Coordinator restarted while the step was running.");
                    progressed = true;
                    break;
                }
                var dependencies = step.DependsOn.Select(id => states[id]).ToArray();
                if (dependencies.Any(d => !InspectionStore.IsTerminal(d.Status))) continue;
                if (step.Type != InspectionStepType.Cleanup && dependencies.Any(d => !IsDependencySuccess(d.Status)))
                {
                    _store.UpdateStep(runId, step.Id, InspectionRunStatus.Skipped, state.Attempt, "A dependency did not pass.");
                    progressed = true;
                    break;
                }
                await ExecuteStepWithRetryAsync(run, bundle, step, state.Attempt, serviceToken).ConfigureAwait(false);
                progressed = true;
                break;
            }
            if (!progressed)
            {
                _store.UpdateRun(runId, InspectionRunStatus.Fail, "No executable step remained; the plan could not converge.");
                return;
            }
        }

        var finalStates = _store.GetSteps(runId);
        var finalRun = _store.GetStoredRun(runId);
        if (finalRun?.CancelRequested == true)
        {
            var cleanupFailed = bundle.Manifest.Plan.Steps.Any(step => step.Type == InspectionStepType.Cleanup
                && finalStates[step.Id].Status != InspectionRunStatus.Pass);
            _store.UpdateRun(runId, InspectionRunStatus.Cancelled, cleanupFailed
                ? "Cancelled; one or more signed cleanup steps did not pass."
                : "Cancelled after signed cleanup completed.");
            return;
        }
        var requiredFailure = bundle.Manifest.Plan.Steps.Any(step => step.Required && !IsDependencySuccess(finalStates[step.Id].Status));
        var hasWarning = bundle.Manifest.Plan.Steps.Any(step => finalStates[step.Id].Status != InspectionRunStatus.Pass);
        var finalStatus = requiredFailure ? InspectionRunStatus.Fail
            : hasWarning ? InspectionRunStatus.Warning : InspectionRunStatus.Pass;
        _store.UpdateRun(runId, finalStatus, finalStatus switch
        {
            InspectionRunStatus.Fail => "필수 자동 점검 중 통과하지 못한 항목이 있습니다. 조치 후 다시 점검하세요.",
            InspectionRunStatus.Warning => "자동 점검만으로 준비 완료를 확정하지 못했습니다. 표시 항목을 확인하고 재점검하세요.",
            _ => "자동 장치 경로 점검을 통과했습니다. 실제 음질은 최종 청취로 확인하세요."
        });
    }

    private async Task ExecuteStepWithRetryAsync(StoredRun run, StoredBundle bundle, InspectionStep step, int previousAttempt, CancellationToken serviceToken)
    {
        Exception? last = null;
        for (var attempt = previousAttempt + 1; attempt <= step.Retry + 1; attempt++)
        {
            _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Running, attempt, "Running.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
            try
            {
                _store.Audit(run.Owner, "step.execute", "started", $"{run.RunId}:{step.Id}:{step.Type}");
                var output = await ExecuteStepAsync(run, bundle, step, timeout.Token).ConfigureAwait(false);
                if (output is InspectionStepWarning warning)
                {
                    _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Warning, attempt, warning.Message, warning.Output);
                    _store.Audit(run.Owner, "step.execute", "warning", $"{run.RunId}:{step.Id}:{warning.Message}");
                    return;
                }
                _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Pass, attempt, StepPassMessage(step), output);
                _store.Audit(run.Owner, "step.execute", "pass", $"{run.RunId}:{step.Id}:{step.Type}");
                return;
            }
            catch (OperationCanceledException) when (!serviceToken.IsCancellationRequested)
            {
                last = new TimeoutException($"Step exceeded {step.TimeoutSeconds} seconds.");
            }
            catch (InspectionRunCancelledException ex)
            {
                _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Cancelled, attempt, ex.Message);
                _store.Audit(run.Owner, "step.execute", "cancelled", $"{run.RunId}:{step.Id}:{ex.Message}");
                return;
            }
            catch (InspectionUnknownException ex)
            {
                _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Unknown, attempt, ex.Message);
                _store.Audit(run.Owner, "step.execute", "unknown", $"{run.RunId}:{step.Id}:{ex.Message}");
                return;
            }
            catch (Exception ex) { last = ex; }
            _store.Audit(run.Owner, "step.execute", "fail", $"{run.RunId}:{step.Id}:{last.Message}");
            _store.AppendEvent(run.RunId, "step.attempt-failed", new { stepId = step.Id, attempt, error = last.Message });
        }
        _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Fail, step.Retry + 1, last?.Message ?? "Step failed.");
        if (!string.IsNullOrWhiteSpace(step.Compensate))
        {
            var compensation = bundle.Manifest.Plan.Steps.Single(s => s.Id == step.Compensate);
            var compensationState = _store.GetSteps(run.RunId)[compensation.Id];
            if (!InspectionStore.IsTerminal(compensationState.Status))
            {
                _store.UpdateStep(run.RunId, compensation.Id, InspectionRunStatus.Running, compensationState.Attempt + 1, $"Compensating for {step.Id}.");
                try
                {
                    var output = await ExecuteStepAsync(run, bundle, compensation, serviceToken).ConfigureAwait(false);
                    _store.UpdateStep(run.RunId, compensation.Id, InspectionRunStatus.Pass, compensationState.Attempt + 1, $"Compensation for {step.Id} passed.", output);
                }
                catch (Exception ex) { _store.UpdateStep(run.RunId, compensation.Id, InspectionRunStatus.Fail, compensationState.Attempt + 1, $"Compensation failed: {ex.Message}"); }
            }
        }
    }

    private async Task<object?> ExecuteStepAsync(StoredRun run, StoredBundle bundle, InspectionStep step, CancellationToken token) => step.Type switch
    {
        InspectionStepType.Wait => await ExecuteWaitAsync(run.RunId, step.Id, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Collect => await ExecuteCollectAsync(run.RunId, step.Id, step.Input, token).ConfigureAwait(false),
        InspectionStepType.AppApi => await ExecuteAppApiAsync(run.RunId, step.Input, token).ConfigureAwait(false),
        InspectionStepType.DesktopProbe => await ExecuteDesktopProbeAsync(run.RunId, step.Id, step.Input, token).ConfigureAwait(false),
        InspectionStepType.AudioProbe => await ExecuteAudioProbeAsync(run.RunId, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Script => await ExecuteScriptAsync(run.RunId, step.Id, bundle.Path, step.Input, step.TimeoutSeconds, token).ConfigureAwait(false),
        InspectionStepType.Privileged or InspectionStepType.Fault => await ExecuteBrokerAsync(run, step.Id, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Checkpoint => await ExecuteCheckpointAsync(run.RunId, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Cleanup => await ExecuteCleanupAsync(run, bundle, step, token).ConfigureAwait(false),
        _ => throw new InvalidOperationException($"Unsupported step type: {step.Type}")
    };

    private async Task<object> ExecuteAudioProbeAsync(string runId, JsonElement input, CancellationToken token)
    {
        var settleSeconds = GetInt(input, "settleSeconds", 0, 0, 60);
        if (settleSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(settleSeconds), token).ConfigureAwait(false);

        var captureReadyTimeoutSeconds = GetInt(input, "captureReadyTimeoutSeconds", 45, 0, 180);
        var signalSettleSeconds = GetInt(input, "signalSettleSeconds", 12, 0, 30);
        var captureDeadline = DateTimeOffset.UtcNow.AddSeconds(captureReadyTimeoutSeconds);
        DateTimeOffset? captureReadySince = null;

        while (true)
        {

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8787/api/audio");
        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        if (!string.IsNullOrWhiteSpace(appToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);
        using var response = await _httpFactory.CreateClient().SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"오디오 진단 API 응답 실패: HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("오디오 진단 응답에 소스 목록이 없습니다.");
        var microphones = sources.EnumerateArray()
            .Where(source => source.TryGetProperty("kind", out var kind) && kind.GetString() == "mic")
            .Select(source => new
            {
                Id = ReadString(source, "id"),
                Name = ReadString(source, "name"),
                DeviceId = ReadString(source, "deviceId"),
                ResolvedDeviceId = ReadString(source, "resolvedDeviceId"),
                ResolvedDeviceName = ReadString(source, "resolvedDeviceName"),
                Available = source.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True,
                Error = ReadString(source, "captureError"),
                Muted = source.TryGetProperty("muted", out var muted) && muted.ValueKind == JsonValueKind.True,
                Gain = source.TryGetProperty("gain", out var gain) && gain.TryGetDouble(out var value) ? value : 0,
                BytesReceived = source.TryGetProperty("bytesReceived", out var bytes) && bytes.TryGetInt64(out var count) ? count : 0,
                SignalWarning = source.TryGetProperty("signalWarning", out var signalWarning) && signalWarning.ValueKind == JsonValueKind.True,
                Loopback = source.TryGetProperty("loopback", out var loopback) && loopback.ValueKind == JsonValueKind.True
                    || IsLoopbackName(ReadString(source, "name"))
            }).ToArray();
        if (microphones.Length == 0)
        {
            if (DateTimeOffset.UtcNow < captureDeadline)
            {
                captureReadySince = null;
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                continue;
            }
            throw new InvalidOperationException($"{_options.Current.Room} · 설정된 마이크 입력이 없습니다.");
        }

        var real = microphones.Where(microphone => !microphone.Loopback).ToArray();
        var loopbacks = microphones.Where(microphone => microphone.Loopback).ToArray();
        if (real.Length == 0)
        {
            if (DateTimeOffset.UtcNow < captureDeadline)
            {
                captureReadySince = null;
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                continue;
            }
            return new InspectionStepWarning(
                $"{_options.Current.Room} · 실제 마이크 대신 스테레오 믹스 계열 입력만 설정되어 물리 마이크 경로를 판정할 수 없습니다.",
                new { room = _options.Current.Room, microphones, verdict = "loopback-only" });
        }

        var roomId = StartupInspectionHostedService.NormalizeRoomId(
            string.IsNullOrWhiteSpace(_options.Current.RoomId) ? _options.Current.Room : _options.Current.RoomId);
        var requiredIds = _options.Current.RequiredMicrophoneDeviceIds
            .FirstOrDefault(entry => string.Equals(entry.Key, roomId, StringComparison.OrdinalIgnoreCase)).Value
            ?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var targets = requiredIds.Length == 0
            ? real
            : real.Where(microphone => requiredIds.Contains(microphone.DeviceId, StringComparer.OrdinalIgnoreCase)
                || requiredIds.Contains(microphone.ResolvedDeviceId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (requiredIds.Length > 0)
        {
            var matched = targets.SelectMany(microphone => new[] { microphone.DeviceId, microphone.ResolvedDeviceId })
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requiredIds.Where(id => !matched.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                if (DateTimeOffset.UtcNow < captureDeadline)
                {
                    captureReadySince = null;
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }
                throw new InvalidOperationException($"{_options.Current.Room} · 필수 마이크 설정을 찾을 수 없습니다: {string.Join(", ", missing)}");
            }
        }

        var disabled = targets.Where(microphone => microphone.Muted || microphone.Gain <= 0).ToArray();
        if (requiredIds.Length > 0 && disabled.Length > 0)
        {
            throw new InvalidOperationException($"{_options.Current.Room} · 필수 마이크가 음소거되어 있거나 입력 음량이 0입니다: {string.Join(", ", disabled.Select(microphone => microphone.Name))}");
        }
        var enabledTargets = targets.Where(microphone => !microphone.Muted && microphone.Gain > 0).ToArray();
        if (enabledTargets.Length == 0)
        {
            throw new InvalidOperationException($"{_options.Current.Room} · 마이크가 음소거되어 있거나 입력 음량이 0입니다: {string.Join(", ", disabled.Select(microphone => microphone.Name))}");
        }

        var readyTargets = enabledTargets.Where(microphone => microphone.Available).ToArray();
        var allRequiredReady = requiredIds.Length == 0
            ? readyTargets.Length > 0
            : targets.All(microphone => microphone.Available);
        if (!allRequiredReady && DateTimeOffset.UtcNow < captureDeadline)
        {
            captureReadySince = null;
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            continue;
        }
        if (!allRequiredReady)
        {
            var unavailable = (requiredIds.Length == 0 ? enabledTargets : targets)
                .Where(microphone => !microphone.Available && !string.IsNullOrWhiteSpace(microphone.Error)).ToArray();
            if (unavailable.Length > 0)
            {
                var names = string.Join(", ", unavailable.Select(microphone => microphone.Name));
                throw new InvalidOperationException($"{_options.Current.Room} · 마이크 연결 실패 — {names} 입력을 열거나 데이터를 받을 수 없습니다. 수신기 전원, USB/오디오 케이블, Windows 입력 장치를 확인하세요.");
            }
            return new InspectionStepWarning(
                $"{_options.Current.Room} · 앱 오디오 믹서가 아직 실제 마이크 캡처를 시작하지 않아 연결 판정을 보류합니다.",
                new { room = _options.Current.Room, microphones, requiredIds, verdict = "not-running" });
        }

        captureReadySince ??= DateTimeOffset.UtcNow;
        var signalReadyAt = captureReadySince.Value.AddSeconds(signalSettleSeconds);
        if (DateTimeOffset.UtcNow < signalReadyAt)
        {
            var remaining = signalReadyAt - DateTimeOffset.UtcNow;
            await Task.Delay(remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining, token).ConfigureAwait(false);
            continue;
        }

        var readinessOnly = GetBool(input, "readinessOnly");
        var ambientSignalWarning = requiredIds.Length == 0
            ? readyTargets.All(microphone => microphone.SignalWarning)
            : readyTargets.Any(microphone => microphone.SignalWarning);
        var ambiguousPrimary = requiredIds.Length == 0 && real.Length > 1;
        if (ambiguousPrimary)
        {
            var ambiguousResult = new
            {
                room = _options.Current.Room,
                microphones,
                requiredIds,
                micWarning = ambientSignalWarning,
                readinessOnly,
                physicalRequested = GetBool(input, "physical"),
                physicalPassed = (bool?)null,
                physicalTests = Array.Empty<object>(),
                verdict = "primary-mic-not-selected"
            };
            return new InspectionStepWarning($"{_options.Current.Room} · 실제 마이크가 여러 개이지만 필수 강의실 마이크가 지정되지 않아 자동 합격을 보류합니다. 사용할 장치 ID를 호실 설정에 지정하세요.", ambiguousResult);
        }

        var runPhysicalProbe = GetBool(input, "physical");
        var physicalTests = new List<object>();
        var physicalIssues = new List<string>();
        bool? physicalPassed = null;
        if (runPhysicalProbe)
        {
            var physicalTimeoutSeconds = GetInt(input, "physicalTimeoutSeconds", 8, 4, 30);
            var physicalTargets = requiredIds.Length > 0 ? readyTargets : readyTargets.Take(1).ToArray();
            foreach (var microphone in physicalTargets)
            {
                var endpointId = !string.IsNullOrWhiteSpace(microphone.ResolvedDeviceId)
                    ? microphone.ResolvedDeviceId
                    : microphone.DeviceId;
                if (string.IsNullOrWhiteSpace(endpointId))
                {
                    physicalIssues.Add($"{microphone.Name}:endpoint-not-resolved");
                    physicalTests.Add(new
                    {
                        microphone.Id,
                        microphone.Name,
                        requestedCaptureEndpointId = endpointId,
                        ok = false,
                        passed = false,
                        reason = "endpoint-not-resolved",
                        error = "The concrete WASAPI capture endpoint ID is unavailable."
                    });
                    continue;
                }

                var audio = await _session.SendAsync("audio-self-test", new
                {
                    captureEndpointId = endpointId,
                    timeoutSeconds = physicalTimeoutSeconds
                }, token).ConfigureAwait(false);
                var payload = audio.Payload?.Clone();
                var passed = audio.Ok && payload is { ValueKind: JsonValueKind.Object }
                    && payload.Value.TryGetProperty("passed", out var passedValue)
                    && passedValue.ValueKind == JsonValueKind.True;
                var isLoopback = payload is { ValueKind: JsonValueKind.Object }
                    && payload.Value.TryGetProperty("isLoopback", out var loopbackValue)
                    && loopbackValue.ValueKind == JsonValueKind.True;
                var loopbackReported = payload is { ValueKind: JsonValueKind.Object }
                    && payload.Value.TryGetProperty("isLoopback", out var reportedLoopback)
                    && reportedLoopback.ValueKind is JsonValueKind.True or JsonValueKind.False;
                var resolvedEndpointId = payload is { ValueKind: JsonValueKind.Object }
                    ? ReadString(payload.Value, "resolvedCaptureEndpointId") : string.Empty;
                var endpointMatches = !string.IsNullOrWhiteSpace(resolvedEndpointId)
                    && string.Equals(endpointId, resolvedEndpointId, StringComparison.OrdinalIgnoreCase);
                var reason = payload is { ValueKind: JsonValueKind.Object }
                    ? ReadString(payload.Value, "reason") : string.Empty;
                var error = payload is { ValueKind: JsonValueKind.Object }
                    ? ReadString(payload.Value, "error") : string.Empty;
                var accepted = passed && loopbackReported && !isLoopback && endpointMatches;
                physicalTests.Add(new
                {
                    microphone.Id,
                    microphone.Name,
                    requestedCaptureEndpointId = endpointId,
                    ok = audio.Ok,
                    passed,
                    accepted,
                    endpointMatches,
                    isLoopback,
                    reason = string.IsNullOrWhiteSpace(reason) ? audio.Error : reason,
                    error = string.IsNullOrWhiteSpace(error) ? audio.Error : error,
                    metrics = payload
                });
                if (!accepted)
                {
                    var issue = !audio.Ok ? "session-agent-error"
                        : !loopbackReported ? "invalid-probe-response"
                        : isLoopback ? "loopback-endpoint"
                        : !endpointMatches ? "endpoint-mismatch"
                        : string.IsNullOrWhiteSpace(reason) ? "physical-path-not-verified" : reason;
                    physicalIssues.Add($"{microphone.Name}:{issue}");
                }
            }
            physicalPassed = physicalTargets.Length > 0 && physicalIssues.Count == 0;
        }

        var primaryPinned = requiredIds.Length > 0;
        var micWarning = readinessOnly || runPhysicalProbe && physicalPassed == true ? false : ambientSignalWarning;
        var verdict = runPhysicalProbe && physicalPassed != true ? "physical-path-not-verified"
            : runPhysicalProbe && !primaryPinned ? "primary-mic-not-pinned"
            : runPhysicalProbe ? "physical-path-verified"
            : readinessOnly ? "capture-ready"
            : micWarning ? "signal-not-detected"
            : loopbacks.Length > 0 ? "loopback-configured" : "healthy";
        if (loopbacks.Length > 0 && runPhysicalProbe && physicalPassed == true)
            verdict = "loopback-configured";
        var result = new
        {
            room = _options.Current.Room,
            microphones,
            requiredIds,
            primaryPinned,
            readinessOnly,
            ambientSignalWarning,
            micWarning,
            physicalRequested = runPhysicalProbe,
            physicalPassed,
            physicalTests,
            verdict
        };
        if (runPhysicalProbe && physicalPassed != true)
        {
            var issueSummary = string.Join(", ", physicalIssues.Distinct(StringComparer.OrdinalIgnoreCase));
            return new InspectionStepWarning(
                $"{_options.Current.Room} · 마이크 조치 필요 — 지정된 실제 마이크에서 자동 시험음을 확인하지 못했습니다. 수신기 전원·배터리·페어링, USB/오디오 케이블, 스피커 출력을 확인하세요.{(string.IsNullOrWhiteSpace(issueSummary) ? string.Empty : $" ({issueSummary})")}",
                result);
        }
        if (runPhysicalProbe && !primaryPinned)
            return new InspectionStepWarning(
                $"{_options.Current.Room} · 자동 시험음은 감지했지만 필수 강의실 마이크 endpoint ID가 호실 설정에 고정되지 않았습니다. 현재 장치 ID를 지정한 뒤 다시 점검하세요.",
                result);
        if (micWarning)
            return new InspectionStepWarning($"{_options.Current.Room} · 실제 마이크 장치는 연결됐지만 현재 입력 신호가 감지되지 않았습니다. 실제 음질은 짧은 녹음 청취로 확인하세요.", result);
        if (loopbacks.Length > 0)
            return new InspectionStepWarning($"{_options.Current.Room} · 실제 마이크는 연결됐지만 스테레오 믹스 계열 입력도 마이크 목록에 남아 있습니다. 자동 판정에서는 제외했습니다.", result);
        return result;
        }
    }

    private async Task<object> ExecuteWaitAsync(string runId, string stepId, JsonElement input, CancellationToken token)
    {
        var seconds = GetInt(input, "seconds", 1, 1, 7200);
        var elapsed = 0;
        while (elapsed < seconds)
        {
            var run = _store.GetStoredRun(runId) ?? throw new InspectionRunCancelledException("Run disappeared.");
            if (run.CancelRequested) throw new InspectionRunCancelledException("Cancelled by operator.");
            if (run.PauseRequested)
            {
                _store.UpdateRun(runId, InspectionRunStatus.Paused, "Paused during wait step.");
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }
            if (run.Status == InspectionRunStatus.Paused) _store.UpdateRun(runId, InspectionRunStatus.Running, "Resumed.");
            var slice = Math.Min(5, seconds - elapsed);
            await Task.Delay(TimeSpan.FromSeconds(slice), token).ConfigureAwait(false);
            elapsed += slice;
            using var app = Process.GetProcessesByName("MediaCaptureHelper").FirstOrDefault();
            _store.AppendEvent(runId, "telemetry.5s", new
            {
                stepId, elapsedSeconds = elapsed, targetSeconds = seconds,
                appRunning = app is not null,
                appWorkingSetBytes = app?.WorkingSet64,
                appPrivateBytes = app?.PrivateMemorySize64,
                appHandles = app?.HandleCount,
                coordinatorWorkingSetBytes = Environment.WorkingSet,
                timestampUtc = DateTimeOffset.UtcNow
            });
        }
        return new { waitedSeconds = seconds };
    }

    private async Task<object> ExecuteCollectAsync(string runId, string stepId, JsonElement input, CancellationToken token)
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { d.Name, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace }).ToArray();
        var snapshot = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            room = _options.Current.Room,
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            user = Environment.UserName,
            interactiveSession = Environment.UserInteractive,
            agentVersion = InspectionProtocol.AgentVersion,
            drives
        };
        return await SaveArtifactAsync(runId, stepId, "system.json", JsonSerializer.SerializeToUtf8Bytes(snapshot, InspectionProtocol.Json), token).ConfigureAwait(false);
    }

    private async Task<object> ExecuteAppApiAsync(string runId, JsonElement input, CancellationToken token)
    {
        var url = ExpandTemplate(RequiredString(input, "url"), runId);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !IPAddressIsLoopback(uri.Host) || uri.Port is not (8787 or 8790))
            throw new InvalidDataException("appApi is restricted to the local application ports.");
        var method = input.TryGetProperty("method", out var methodValue) ? methodValue.GetString() ?? "GET" : "GET";
        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        var requestBody = input.TryGetProperty("body", out var body) ? ExpandTemplate(body.GetRawText(), runId) : null;
        var expected = GetInt(input, "expectedStatus", 200, 100, 599);
        var waitForReadySeconds = GetInt(input, "waitForReadySeconds", 0, 0, 300);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitForReadySeconds);
        Exception? lastConnectionError = null;

        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), uri);
                if (!string.IsNullOrWhiteSpace(appToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);
                    if (uri.Port == 8787 && uri.AbsolutePath.Equals("/api/inspection/report", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", appToken);
                }
                if (requestBody is not null)
                    request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

                using var response = await _httpFactory.CreateClient().SendAsync(request, token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if ((int)response.StatusCode == expected)
                    return new { status = (int)response.StatusCode, body = text[..Math.Min(text.Length, 65536)] };

                var transient = (int)response.StatusCode is 408 or 425 or 429 or >= 500;
                if (!transient || DateTimeOffset.UtcNow >= deadline)
                    throw new InvalidOperationException($"Expected HTTP {expected}, received {(int)response.StatusCode}.");
                lastConnectionError = new HttpRequestException($"Application returned HTTP {(int)response.StatusCode} while starting.");
            }
            catch (HttpRequestException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastConnectionError = ex;
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException($"Application did not become ready within {waitForReadySeconds} seconds.", lastConnectionError);
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
    }

    private async Task<object> ExecuteCheckpointAsync(string runId, JsonElement input, CancellationToken token)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("ack", out var ackValue) || string.IsNullOrWhiteSpace(ackValue.GetString()))
            return new { checkpoint = true, atUtc = DateTimeOffset.UtcNow };
        var channel = ackValue.GetString()!;
        while (!_store.HasAck(runId, channel))
        {
            var run = _store.GetStoredRun(runId) ?? throw new InspectionRunCancelledException("Run disappeared.");
            if (run.CancelRequested) throw new InspectionRunCancelledException("Cancelled by operator.");
            if (run.PauseRequested) { _store.UpdateRun(runId, InspectionRunStatus.Paused, $"Paused while waiting for {channel} acknowledgement."); await Task.Delay(1000, token).ConfigureAwait(false); continue; }
            if (run.Status == InspectionRunStatus.Paused) _store.UpdateRun(runId, InspectionRunStatus.Running, "Resumed.");
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        return new { checkpoint = true, ack = channel, received = true, atUtc = DateTimeOffset.UtcNow };
    }

    private async Task<object> ExecuteDesktopProbeAsync(string runId, string stepId, JsonElement input, CancellationToken token)
    {
        var monitors = await _session.SendAsync("monitors", null, token).ConfigureAwait(false);
        if (!monitors.Ok) throw new InvalidOperationException(monitors.Error);
        var selfTest = await _session.SendAsync("self-test", null, token).ConfigureAwait(false);
        if (!selfTest.Ok || selfTest.Payload is null) throw new InvalidOperationException(selfTest.Error ?? "Desktop self-test failed.");
        if (selfTest.Payload.Value.TryGetProperty("consoleWindowVisible", out var consoleVisible) && consoleVisible.GetBoolean())
            throw new InvalidOperationException("Session agent unexpectedly owns a visible console window.");
        JsonElement? audioProbe = null;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("audioPhysical", out var physical) && physical.ValueKind == JsonValueKind.True)
        {
            var captureEndpointId = RequiredString(input, "captureEndpointId");
            var audio = await _session.SendAsync("audio-self-test", new
            {
                captureEndpointId,
                timeoutSeconds = GetInt(input, "physicalTimeoutSeconds", 8, 4, 30)
            }, token).ConfigureAwait(false);
            if (!audio.Ok || audio.Payload is null) throw new InvalidOperationException(audio.Error ?? "Physical audio probe failed.");
            audioProbe = audio.Payload;
            if (!audio.Payload.Value.TryGetProperty("passed", out var passed) || !passed.GetBoolean()) throw new InvalidOperationException("Speaker-to-microphone correlation was below the physical-path threshold.");
        }
        var capture = await _session.SendAsync("capture", new { monitor = GetInt(input, "monitor", 0, 0, 31), maxWidth = 1280, quality = 75 }, token).ConfigureAwait(false);
        if (!capture.Ok || capture.Payload is null) throw new InvalidOperationException(capture.Error ?? "Desktop capture failed.");
        var base64 = capture.Payload.Value.GetProperty("jpegBase64").GetString() ?? throw new InvalidDataException("Desktop frame is empty.");
        var capturedAt = capture.Payload.Value.GetProperty("capturedAtUtc").GetDateTimeOffset();
        if (DateTimeOffset.UtcNow - capturedAt > TimeSpan.FromSeconds(2)) throw new InvalidDataException("Desktop frame is stale.");
        var artifact = await SaveArtifactAsync(runId, stepId, "desktop.jpg", Convert.FromBase64String(base64), token).ConfigureAwait(false);
        return new { monitors = monitors.Payload, selfTest = selfTest.Payload, audioProbe, artifact };
    }

    private async Task<object> ExecuteScriptAsync(string runId, string stepId, string bundleRoot, JsonElement input, int timeoutSeconds, CancellationToken token)
    {
        var relative = RequiredString(input, "path").Replace('/', Path.DirectorySeparatorChar);
        var script = Path.GetFullPath(Path.Combine(bundleRoot, relative));
        if (!script.StartsWith(Path.GetFullPath(bundleRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Script path must be a .ps1 inside the verified bundle.");
        if (!File.Exists(script)) throw new FileNotFoundException("Bundle script was not found.", script);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
        };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("RemoteSigned"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        if (input.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            foreach (var value in arguments.EnumerateArray()) start.ArgumentList.Add(value.GetString() ?? string.Empty);
        start.Environment["SILENTSTREAM_INSPECTION_ROOT"] = _options.Root;
        start.Environment["SILENTSTREAM_APP_CONFIG"] = _options.Current.AppConfigPath;
        start.Environment["SILENTSTREAM_APPDATA"] = string.IsNullOrWhiteSpace(_options.Current.AppConfigPath) ? string.Empty : Path.GetDirectoryName(_options.Current.AppConfigPath)!;
        start.Environment["SILENTSTREAM_APP_EXE"] = _options.Current.AppExecutablePath;
        start.Environment["SILENTSTREAM_CACHED_INSTALLER"] = _options.Current.CachedInstallerPath;
        start.Environment["SILENTSTREAM_RUN_ID"] = runId;
        start.Environment["SILENTSTREAM_STEP_ID"] = stepId;
        var result = await LimitedProcess.RunAsync(start, TimeSpan.FromSeconds(timeoutSeconds), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Script exited with {result.ExitCode}: {result.StandardError}");
        return new { exitCode = result.ExitCode, stdout = result.StandardOutput, stderr = result.StandardError };
    }

    private async Task<object> ExecuteBrokerAsync(StoredRun run, string stepId, JsonElement input, CancellationToken token)
    {
        if (!run.DestructiveApproved) throw new UnauthorizedAccessException("This run has no destructive-operation approval.");
        var action = RequiredString(input, "action");
        if (!BrokerActions.Contains(action)) throw new InvalidDataException("Privileged action is not in the broker schema.");
        if (JustInTimeApprovalActions.Contains(action))
            await WaitForDestructiveApprovalAsync(run.RunId, stepId, action, token).ConfigureAwait(false);
        if (action == "reboot")
        {
            var autoLogon = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")?.GetValue("AutoAdminLogon")?.ToString();
            if (!string.Equals(autoLogon, "1", StringComparison.Ordinal))
                throw new InspectionUnknownException("Windows 자동 로그인이 설정되어 있지 않아 재부팅 데스크톱 점검을 실행하지 않았습니다.");
            try
            {
                var ping = await _session.SendAsync("ping", null, token).ConfigureAwait(false);
                if (!ping.Ok) throw new InvalidOperationException();
            }
            catch { throw new InspectionUnknownException("로그인 사용자 세션 에이전트가 없어 재부팅을 실행하지 않았습니다."); }
        }
        var payload = input.TryGetProperty("payload", out var value) ? value : (JsonElement?)null;
        var response = await _broker.SendAsync(action, payload, $"approved:{run.RunId}", token).ConfigureAwait(false);
        if (!response.Ok) throw new InvalidOperationException(response.Message);
        SessionResponse? sessionResponse = action switch
        {
            "restart-app" => await _session.SendAsync("start-app", response.Payload, token).ConfigureAwait(false),
            "install-app" => await _session.SendAsync("install-app", response.Payload, token).ConfigureAwait(false),
            "uninstall-app" => await _session.SendAsync("uninstall-app", response.Payload, token).ConfigureAwait(false),
            _ => null
        };
        if (sessionResponse is { Ok: false }) throw new InvalidOperationException(sessionResponse.Error);
        return new { response.Message, broker = response.Payload, session = sessionResponse?.Payload };
    }

    private async Task<object?> ExecuteCleanupAsync(StoredRun run, StoredBundle bundle, InspectionStep step, CancellationToken token)
    {
        if (step.Input.TryGetProperty("action", out _)) return await ExecuteBrokerAsync(run, step.Id, step.Input, token).ConfigureAwait(false);
        if (step.Input.TryGetProperty("path", out _)) return await ExecuteScriptAsync(run.RunId, step.Id, bundle.Path, step.Input, step.TimeoutSeconds, token).ConfigureAwait(false);
        return new { cleanup = "no-op" };
    }

    private async Task WaitForDestructiveApprovalAsync(string runId, string stepId, string action, CancellationToken token)
    {
        var channel = $"destructive-{stepId}";
        _store.AppendEvent(runId, "destructive.approval-required", new { stepId, action });
        while (!_store.HasAck(runId, channel))
        {
            var current = _store.GetStoredRun(runId) ?? throw new InspectionRunCancelledException("Run disappeared.");
            if (current.CancelRequested) throw new InspectionRunCancelledException("Cancelled while waiting for destructive approval.");
            _store.UpdateRun(runId, current.PauseRequested ? InspectionRunStatus.Paused : InspectionRunStatus.Running,
                $"Waiting for an admin to approve {action} at step {stepId}.");
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        _store.AppendEvent(runId, "destructive.approved", new { stepId, action });
    }

    private async Task<object> SaveArtifactAsync(string runId, string stepId, string name, byte[] bytes, CancellationToken token)
    {
        var artifactId = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(runId, $"{artifactId}-{Path.GetFileName(name)}");
        var output = Path.GetFullPath(Path.Combine(_options.ArtifactsPath, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, bytes, token).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _store.AddArtifact(artifactId, runId, stepId, relative, hash, bytes.LongLength);
        return new { artifactId, name, sha256 = hash, length = bytes.LongLength };
    }

    private static string RequiredString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"Missing input.{name}.");

    private static int GetInt(JsonElement input, string name, int fallback, int min, int max) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max) : fallback;

    private static bool GetBool(JsonElement input, string name, bool fallback = false) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.True ? true
                : value.ValueKind == JsonValueKind.False ? false : fallback
            : fallback;

    private static string StepPassMessage(InspectionStep step) => step.Type switch
    {
        InspectionStepType.AudioProbe when GetBool(step.Input, "physical") =>
            "지정된 실제 마이크에서 자동 시험음을 감지했습니다. 실제 음질은 최종 청취로 확인하세요.",
        InspectionStepType.AudioProbe when GetBool(step.Input, "readinessOnly") =>
            "지정된 마이크 장치가 연결되어 입력 준비가 된 것을 확인했습니다.",
        _ => "통과했습니다."
    };

    private static bool IPAddressIsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    private static bool IsDependencySuccess(InspectionRunStatus status) =>
        status is InspectionRunStatus.Pass or InspectionRunStatus.Warning;

    private static string ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;

    private static bool IsLoopbackName(string value) => Regex.IsMatch(value,
        "stereo\\s*mix|스테레오\\s*믹스|what\\s*u\\s*hear|wave\\s*out|loopback|monitor\\s+of",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string ExpandTemplate(string value, string runId)
    {
        var now = DateTime.Now;
        return TemplateToken.Replace(value, match =>
        {
            var token = match.Groups["token"].Value;
            if (token == "runId") return runId;
            if (token == "room") return JsonEscape(_options.Current.Room);
            var local = Regex.Match(token, @"^local\+(?<seconds>\d+)s:(?<format>.+)$", RegexOptions.CultureInvariant);
            if (local.Success) return now.AddSeconds(int.Parse(local.Groups["seconds"].Value)).ToString(local.Groups["format"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (token.StartsWith("stepDate:", StringComparison.Ordinal))
            {
                var parts = token.Split(':', 4);
                if (parts.Length != 4 || !int.TryParse(parts[3], out var delta)) throw new InvalidDataException($"Invalid stepDate template: {token}");
                var raw = ResolveStepValue(runId, parts[1], parts[2]);
                if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var date))
                    throw new InvalidDataException($"Step date is invalid: {parts[1]}.{parts[2]}");
                return date.AddSeconds(delta).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (token.StartsWith("step:", StringComparison.Ordinal))
            {
                var parts = token.Split(':', 3);
                if (parts.Length != 3) throw new InvalidDataException($"Invalid step template: {token}");
                return JsonEscape(ResolveStepValue(runId, parts[1], parts[2]));
            }
            throw new InvalidDataException($"Unknown template token: {token}");
        });
    }

    private string ResolveStepValue(string runId, string stepId, string path)
    {
        var current = _store.GetStepOutput(runId, stepId) ?? throw new InvalidDataException($"Step output is unavailable: {stepId}");
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.String)
            {
                var text = current.GetString();
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"Step output path is empty: {stepId}.{path}");
                current = JsonDocument.Parse(text).RootElement.Clone();
            }
            current = current.ValueKind switch
            {
                JsonValueKind.Object when current.TryGetProperty(segment, out var property) => property.Clone(),
                JsonValueKind.Array when int.TryParse(segment, out var index) && index >= 0 && index < current.GetArrayLength() => current[index].Clone(),
                _ => throw new InvalidDataException($"Step output path does not exist: {stepId}.{path}")
            };
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString()! : current.ToString();
    }

    private static string JsonEscape(string value)
    {
        var encoded = JsonSerializer.Serialize(value, InspectionProtocol.Json);
        return encoded.Length >= 2 ? encoded[1..^1] : value;
    }
}

public sealed class InspectionUnknownException(string message) : Exception(message);
public sealed class InspectionRunCancelledException(string message) : Exception(message);
public sealed record InspectionStepWarning(string Message, object Output);
