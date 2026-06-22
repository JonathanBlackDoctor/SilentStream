using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// JSON-file config persistence (%AppData%\MediaCaptureHelper\config.json, plan §3.10/§6).
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
                // True fresh install: seed the room label (호실명) from the machine name so each PC
                // starts distinguishable in a shared-channel deployment; the operator renames it in
                // settings. Done ONLY here — never in CreateDefault() — so the corrupt-file fallback
                // below (which also calls CreateDefault) can never silently stamp a hostname onto a
                // machine whose config merely failed to parse.
                var fresh = AppConfig.CreateDefault();
                fresh.DeviceName = Environment.MachineName;
                return WithRuntimeDefaults(fresh);
            }

            try
            {
                var json = File.ReadAllText(_configFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                return WithRuntimeDefaults(config ?? AppConfig.CreateDefault());
            }
            catch (JsonException)
            {
                // Keep the broken file for diagnosis, then fall back to defaults. The backup copy
                // is best-effort: a locked/unwritable .bak must never turn a recoverable corruption
                // into a hard startup crash (the whole point of this fallback).
                try
                {
                    File.Copy(_configFile, _configFile + ".bak", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
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

    public void Update(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        // The lock is reentrant (Monitor), so Load/Save re-acquire it safely; holding it across
        // the whole read-modify-write makes concurrent edits from different threads serialise
        // instead of clobbering each other (lost-update race).
        lock (_gate)
        {
            var config = Load();
            mutate(config);
            Save(config);
        }
    }

    private static AppConfig WithRuntimeDefaults(AppConfig config)
    {
        // An explicit JSON null section (hand-edited / partially-written file) deserializes to a
        // null property — System.Text.Json does not re-run the C# initializer — so guard every
        // section before dereferencing, or a bad file would NRE straight out of the auto-start path.
        config.YouTube ??= new YouTubeConfig();
        config.Encoding ??= new EncodingConfig();
        config.Recording ??= new RecordingConfig();

        if (string.IsNullOrWhiteSpace(config.Recording.Folder))
        {
            config.Recording.Folder = AppPaths.DefaultRecordingFolder;
        }

        // v2 migration (확장계획서 §6): a v1 file omits these sections, leaving the C#
        // initializers in place — but an explicit JSON null would null them, so guard.
        config.Periods ??= new PeriodsConfig();
        config.Remote ??= new RemoteConfig();

        // A file written before the CloudflareProtocol field existed (or one that nulls/blanks it)
        // must not pass an empty --protocol; default to http2 so UDP-blocked networks still reach
        // the tunnel (2nd field test). An explicit "quic"/"auto" is preserved.
        if (string.IsNullOrWhiteSpace(config.Remote.CloudflareProtocol))
        {
            config.Remote.CloudflareProtocol = "http2";
        }

        // v3 migration: multi-source audio mixer + capture monitor/region. Guard explicit nulls,
        // then build the source list from the legacy single-mic volume fields if it is empty so a
        // v2 file (or a fresh default) yields "system loopback + default microphone".
        config.Audio ??= new AudioConfig();
        config.Audio.Sources ??= new List<AudioSourceConfig>();
        config.Audio.Filters ??= new AudioFiltersConfig();
        config.Capture ??= new CaptureConfig();
        if (config.Audio.Sources.Count == 0)
        {
            config.Audio.Sources.Add(new AudioSourceConfig
            {
                Kind = "system", DeviceId = null, Name = "시스템 소리", Gain = config.Audio.SystemVolume
            });
            config.Audio.Sources.Add(new AudioSourceConfig
            {
                Kind = "mic", DeviceId = config.Audio.MicDeviceId, Name = "마이크", Gain = config.Audio.MicVolume
            });
        }

        if (config.Version < 3)
        {
            config.Version = 3;
        }

        // v4 migration: the first-run "seed every real microphone" step (App layer) keys off
        // Audio.MicsSeeded. A pre-existing config (loaded from a file written by v1-v3) already
        // reflects the user's chosen mics, so mark it seeded — only a brand-new install (Version
        // already 4 from CreateDefault, flag still false) should auto-expand to all microphones.
        if (config.Version < 4)
        {
            config.Audio.MicsSeeded = true;
            config.Version = 4;
        }

        // v5 migration: per-device room label (호실명) for multi-PC single-channel deployments.
        // Additive only — DeviceName defaults to "" and an existing file keeps it empty, so a running
        // deployment is never silently stamped with a hostname. Only a brand-new install seeds the
        // machine name, and that happens in Load's no-file branch (not here, not in CreateDefault).
        if (config.Version < 5)
        {
            config.Version = 5;
        }
        return config;
    }
}
