using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// JSON-file config persistence (%AppData%\SilentStream\config.json, plan §3.10/§6).
/// Saves atomically (temp file + move). A corrupt file is backed up and replaced with
/// defaults so a bad write can never brick the auto-start path.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _configFile;
    private readonly object _gate = new();

    public ConfigStore() : this(AppPaths.ConfigFile) { }

    /// <summary>Test seam: redirect the config file location.</summary>
    public ConfigStore(string configFile) => _configFile = configFile;

    public AppConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_configFile))
            {
                return WithRuntimeDefaults(AppConfig.CreateDefault());
            }

            try
            {
                var json = File.ReadAllText(_configFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                return WithRuntimeDefaults(config ?? AppConfig.CreateDefault());
            }
            catch (JsonException)
            {
                // Keep the broken file for diagnosis, then fall back to defaults.
                File.Copy(_configFile, _configFile + ".bak", overwrite: true);
                return WithRuntimeDefaults(AppConfig.CreateDefault());
            }
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_configFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _configFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, _configFile, overwrite: true);
        }
    }

    private static AppConfig WithRuntimeDefaults(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Recording.Folder))
        {
            config.Recording.Folder = AppPaths.DefaultRecordingFolder;
        }
        return config;
    }
}
