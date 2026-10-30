using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class AgentUpdateHostedService : BackgroundService
{
    private readonly CoordinatorOptionsStore _options;
    private readonly IBrokerClient _broker;
    private readonly InspectionStore _store;
    private readonly StartupUpdateGate _startup;
    private readonly ILogger<AgentUpdateHostedService> _logger;
    private readonly Func<bool> _appRunning;

    public AgentUpdateHostedService(CoordinatorOptionsStore options, IBrokerClient broker, InspectionStore store,
        StartupUpdateGate startup, ILogger<AgentUpdateHostedService> logger)
        : this(options, broker, store, startup, logger, AgentUpdateSafety.ApplicationIsRunning) { }

    internal AgentUpdateHostedService(CoordinatorOptionsStore options, IBrokerClient broker, InspectionStore store,
        StartupUpdateGate startup, ILogger<AgentUpdateHostedService> logger, Func<bool> appRunning)
    {
        _options = options; _broker = broker; _store = store; _startup = startup; _logger = logger;
        _appRunning = appRunning;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ImportUpdatesFromCurrentApp();
                ImportUpdateResults();
                var candidates = Directory.EnumerateFiles(_options.IncomingAgentUpdatesPath, "*.zip", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal).ToArray();
                var result = candidates.Length == 0
                    ? AgentUpdateScanResult.None
                    : await ProcessCandidatesAsync(candidates, stoppingToken).ConfigureAwait(false);
                if (result == AgentUpdateScanResult.Scheduled)
                {
                    _startup.MarkApplying("서명된 점검 서비스 업데이트를 적용하고 있습니다.");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                    continue; // the updater normally replaces/restarts this service
                }
                if (result == AgentUpdateScanResult.AppRunning)
                {
                    // A deferred update must not hold boot diagnostics hostage while an app is
                    // already running. The package remains queued for the next app-free window.
                    _startup.MarkReady(true, "실행 중인 앱을 유지하며 도우미 업데이트를 다음 종료 후로 보류합니다.");
                }
                else if (result == AgentUpdateScanResult.Deferred)
                {
                    _startup.MarkApplying("점검 서비스 업데이트의 안전한 적용 조건을 기다립니다. 실행 중인 앱은 유지합니다.");
                }
                else if (result == AgentUpdateScanResult.Rejected)
                {
                    _startup.MarkReady(false, "일부 점검 서비스 업데이트가 검증을 통과하지 못해 현재 버전으로 계속합니다.");
                }
                else
                {
                    _startup.MarkReady(true, candidates.Length == 0
                        ? "점검 서비스 업데이트 확인을 마쳤습니다."
                        : "점검 서비스가 최신 버전입니다.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inspection-agent update scan failed.");
                _startup.MarkReady(false, $"점검 서비스 업데이트 확인 실패: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<AgentUpdateScanResult> ProcessCandidatesAsync(IReadOnlyList<string> candidates, CancellationToken token)
    {
        // The session helper may be the app's parent. Ending its scheduled task/tree while the
        // app runs can end the recording too. Keep the signed package for the next safe startup.
        if (_appRunning()) return AgentUpdateScanResult.AppRunning;
        var verifier = new InspectionAgentUpdateVerifier(_options.Current.PublicKeys);
        var acquired = new List<AcquiredAgentUpdate>();
        var rejected = false;
        var deferred = false;

        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var applying = candidate + ".applying";
            try
            {
                File.Move(candidate, applying, true);
            }
            catch (IOException ex)
            {
                deferred = true;
                _logger.LogDebug(ex, "Agent update {Path} is still being copied; retrying later.", candidate);
                continue;
            }

            try
            {
                var verified = verifier.VerifyAndExtract(applying, _options.StagedAgentUpdatesPath);
                var version = Version.Parse(verified.Manifest.Version);
                if (version <= Version.Parse(InspectionProtocol.AgentVersion))
                {
                    MoveAside(applying, ".ignored");
                    _store.Audit("system", "agent-update", "ignored", verified.Manifest.Version);
                    continue;
                }
                acquired.Add(new AcquiredAgentUpdate(candidate, applying, version, verified));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                RestoreCandidate(applying, candidate);
                throw;
            }
            catch (Exception ex)
            {
                rejected = true;
                MoveAside(applying, ".failed");
                _store.Audit("system", "agent-update", "denied", $"{Path.GetFileName(candidate)}:{ex.Message}");
                _logger.LogWarning(ex, "Agent update {Path} failed signature/content verification.", candidate);
            }
        }

        if (acquired.Count == 0)
            return deferred ? AgentUpdateScanResult.Deferred
                : rejected ? AgentUpdateScanResult.Rejected : AgentUpdateScanResult.None;

        var selected = acquired.OrderByDescending(item => item.Version).First();
        foreach (var superseded in acquired.Where(item => !ReferenceEquals(item, selected)))
        {
            MoveAside(superseded.ApplyingPath, ".superseded");
            _store.Audit("system", "agent-update", "superseded",
                $"{superseded.Verified.Manifest.Version}:selected-{selected.Verified.Manifest.Version}");
        }

        if (!_store.TryBeginMaintenance(out var blockingRunId))
        {
            RestoreCandidate(selected.ApplyingPath, selected.OriginalPath);
            _store.Audit("system", "agent-update", "deferred", blockingRunId is null
                ? "maintenance-already-reserved" : $"inspection-run-active:{blockingRunId}");
            return AgentUpdateScanResult.Deferred;
        }

        var scheduled = false;
        try
        {
            _store.Audit("system", "agent-update", "verified",
                $"{selected.Verified.Manifest.Version}:{selected.Verified.PackageSha256}");
            var response = await _broker.SendAsync("apply-inspection-update",
                new { packagePath = selected.ApplyingPath },
                $"signed-update:{selected.Verified.PackageSha256}", token).ConfigureAwait(false);
            if (!response.Ok)
            {
                MoveAside(selected.ApplyingPath, ".failed");
                _store.Audit("system", "agent-update", "denied",
                    $"{Path.GetFileName(selected.OriginalPath)}:{response.Message}");
                return AgentUpdateScanResult.Rejected;
            }

            scheduled = true;
            _store.AppendEvent(null, "agent-update.scheduled", new
            {
                selected.Verified.Manifest.Version,
                selected.Verified.PackageSha256
            });
            return AgentUpdateScanResult.Scheduled;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            RestoreCandidate(selected.ApplyingPath, selected.OriginalPath);
            _store.Audit("system", "agent-update", "deferred",
                $"broker-not-ready:{Path.GetFileName(selected.OriginalPath)}:{ex.Message}");
            _logger.LogWarning(ex, "Inspection update broker is not ready; the signed update will be retried.");
            return AgentUpdateScanResult.Deferred;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RestoreCandidate(selected.ApplyingPath, selected.OriginalPath);
            throw;
        }
        catch (Exception ex)
        {
            MoveAside(selected.ApplyingPath, ".failed");
            _store.Audit("system", "agent-update", "denied",
                $"{Path.GetFileName(selected.OriginalPath)}:{ex.Message}");
            _logger.LogError(ex, "Inspection update was rejected after verification.");
            return AgentUpdateScanResult.Rejected;
        }
        finally
        {
            if (!scheduled) _store.EndMaintenance();
        }
    }

    private static void MoveAside(string path, string suffix)
    {
        if (File.Exists(path)) File.Move(path, path + suffix, true);
    }

    private static void RestoreCandidate(string applyingPath, string originalPath)
    {
        try { if (File.Exists(applyingPath)) File.Move(applyingPath, originalPath, true); }
        catch { /* the next scan also discovers a surviving .zip; preserve service availability */ }
    }

    private void ImportUpdatesFromCurrentApp()
    {
        if (string.IsNullOrWhiteSpace(_options.Current.AppExecutablePath)) return;
        var appDirectory = Path.GetDirectoryName(_options.Current.AppExecutablePath);
        if (string.IsNullOrWhiteSpace(appDirectory)) return;
        var source = Path.Combine(appDirectory, "inspection-update");
        if (!Directory.Exists(source)) return;
        foreach (var path in Directory.EnumerateFiles(source, "*.zip", SearchOption.TopDirectoryOnly))
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var target = Path.Combine(_options.IncomingAgentUpdatesPath, $"app-{hash}.zip");
            if (!File.Exists(target) && !Directory.EnumerateFiles(_options.IncomingAgentUpdatesPath, $"app-{hash}.zip*", SearchOption.TopDirectoryOnly).Any())
                File.Copy(path, target, false);
        }
    }

    private void ImportUpdateResults()
    {
        foreach (var path in Directory.EnumerateFiles(_options.UpdateResultsPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var success = root.TryGetProperty("success", out var value) && value.GetBoolean();
                _store.Audit("system", "agent-update.result", success ? "ok" : "rollback", root.GetRawText());
                _store.AppendEvent(null, "agent-update.result", root.Clone());
                File.Move(path, path + ".recorded", true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Agent update result {Path} could not be imported.", path); }
        }
    }

    internal enum AgentUpdateScanResult { None, Scheduled, Deferred, Rejected, AppRunning }
    private sealed record AcquiredAgentUpdate(string OriginalPath, string ApplyingPath, Version Version,
        VerifiedInspectionAgentUpdate Verified);
}
