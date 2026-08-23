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
    private readonly SessionAgentClient _session;
    private readonly BrokerClient _broker;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<InspectionEngine> _logger;

    public InspectionEngine(InspectionStore store, CoordinatorOptionsStore options, SessionAgentClient session,
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
                if (step.Type != InspectionStepType.Cleanup && dependencies.Any(d => d.Status != InspectionRunStatus.Pass))
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
        var requiredFailure = bundle.Manifest.Plan.Steps.Any(step => step.Required && finalStates[step.Id].Status != InspectionRunStatus.Pass);
        _store.UpdateRun(runId, requiredFailure ? InspectionRunStatus.Fail : InspectionRunStatus.Pass,
            requiredFailure ? "One or more required steps did not pass." : "All required steps passed.");
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
                _store.UpdateStep(run.RunId, step.Id, InspectionRunStatus.Pass, attempt, "Passed.", output);
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
        InspectionStepType.Script => await ExecuteScriptAsync(run.RunId, step.Id, bundle.Path, step.Input, step.TimeoutSeconds, token).ConfigureAwait(false),
        InspectionStepType.Privileged or InspectionStepType.Fault => await ExecuteBrokerAsync(run, step.Id, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Checkpoint => await ExecuteCheckpointAsync(run.RunId, step.Input, token).ConfigureAwait(false),
        InspectionStepType.Cleanup => await ExecuteCleanupAsync(run, bundle, step, token).ConfigureAwait(false),
        _ => throw new InvalidOperationException($"Unsupported step type: {step.Type}")
    };

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
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        if (!string.IsNullOrWhiteSpace(appToken)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);
        if (input.TryGetProperty("body", out var body))
        {
            var raw = ExpandTemplate(body.GetRawText(), runId);
            request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await _httpFactory.CreateClient().SendAsync(request, token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var expected = GetInt(input, "expectedStatus", 200, 100, 599);
        if ((int)response.StatusCode != expected) throw new InvalidOperationException($"Expected HTTP {expected}, received {(int)response.StatusCode}.");
        return new { status = (int)response.StatusCode, body = text[..Math.Min(text.Length, 65536)] };
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
            var audio = await _session.SendAsync("audio-self-test", null, token).ConfigureAwait(false);
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

    private static bool IPAddressIsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

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
