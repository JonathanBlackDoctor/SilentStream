using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class PreseedBundleHostedService : IHostedService
{
    private readonly CoordinatorOptionsStore _options;
    private readonly InspectionStore _store;
    private readonly ILogger<PreseedBundleHostedService> _logger;

    public PreseedBundleHostedService(CoordinatorOptionsStore options, InspectionStore store, ILogger<PreseedBundleHostedService> logger)
    {
        _options = options; _store = store; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var verifier = new InspectionBundleVerifier(_options.Current.PublicKeys);
        foreach (var path in Directory.EnumerateFiles(_options.PreseedBundlesPath, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var verified = verifier.VerifyAndExtract(path, _options.BundlesPath);
                var existing = _store.GetBundle(verified.Manifest.BundleId);
                if (existing is null || Version.Parse(verified.Manifest.Version) >= Version.Parse(existing.Manifest.Version))
                {
                    _store.SaveBundle(verified);
                    _store.Audit("system", "bundle.preseed", "ok", $"{verified.Manifest.BundleId}:{verified.Manifest.Version}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preseed inspection bundle {Path} was rejected.", path);
                _store.Audit("system", "bundle.preseed", "denied", $"{Path.GetFileName(path)}:{ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
