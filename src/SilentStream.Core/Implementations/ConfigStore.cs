using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real config.json persistence + DPAPI token encryption lands in Phase 1.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    public AppConfig Load() =>
        throw new NotImplementedException("ConfigStore.Load — Phase 1.");

    public void Save(AppConfig config) =>
        throw new NotImplementedException("ConfigStore.Save — Phase 1.");
}
