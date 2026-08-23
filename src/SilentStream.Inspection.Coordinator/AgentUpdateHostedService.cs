using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class AgentUpdateHostedService : BackgroundService
{
    private readonly CoordinatorOptionsStore _options;
    private readonly BrokerClient _broker;
    private readonly InspectionStore _store;
    private readonly ILogger<AgentUpdateHostedService> _logger;

    public AgentUpdateHostedService(CoordinatorOptionsStore options, BrokerClient broker, InspectionStore store, ILogger<AgentUpdateHostedService> logger)
    {
        _options = options; _broker = broker; _store = store; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ImportUpdatesFromCurrentApp();
                ImportUpdateResults();
                var candidate = Directory.EnumerateFiles(_options.IncomingAgentUpdatesPath, "*.zip", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal).FirstOrDefault();
                if (candidate is not null) await ApplyCandidateAsync(candidate, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Inspection-agent update scan failed."); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyCandidateAsync(string candidate, CancellationToken token)
    {
        var applying = candidate + ".applying";
        File.Move(candidate, applying, true);
        try
        {
            var verifier = new InspectionAgentUpdateVerifier(_options.Current.PublicKeys);
            var verified = verifier.VerifyAndExtract(applying, _options.StagedAgentUpdatesPath);
            if (Version.Parse(verified.Manifest.Version) <= Version.Parse(InspectionProtocol.AgentVersion))
            {
                File.Move(applying, applying + ".ignored", true);
                _store.Audit("system", "agent-update", "ignored", verified.Manifest.Version);
                return;
            }
            _store.Audit("system", "agent-update", "verified", $"{verified.Manifest.Version}:{verified.PackageSha256}");
            var response = await _broker.SendAsync("apply-inspection-update", new { packagePath = applying },
                $"signed-update:{verified.PackageSha256}", token).ConfigureAwait(false);
            if (!response.Ok) throw new InvalidOperationException(response.Message);
            _store.AppendEvent(null, "agent-update.scheduled", new { verified.Manifest.Version, verified.PackageSha256 });
        }
        catch (Exception ex)
        {
            try { if (File.Exists(applying)) File.Move(applying, applying + ".failed", true); } catch { }
            _store.Audit("system", "agent-update", "denied", $"{Path.GetFileName(candidate)}:{ex.Message}");
            throw;
        }
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
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
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
}
