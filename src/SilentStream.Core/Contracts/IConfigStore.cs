using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Loads and persists <see cref="AppConfig"/> from config.json, encrypting the OAuth
/// refresh token with DPAPI. See plan §3.10 / §6.
/// </summary>
public interface IConfigStore
{
    /// <summary>Loads the config, creating defaults if none exists.</summary>
    AppConfig Load();

    /// <summary>Persists the config atomically.</summary>
    void Save(AppConfig config);

    /// <summary>
    /// Atomically load → mutate → save under a single lock, so concurrent editors (control
    /// window UI thread + remote-control Kestrel threads) cannot lose each other's updates.
    /// </summary>
    void Update(Action<AppConfig> mutate);
}
