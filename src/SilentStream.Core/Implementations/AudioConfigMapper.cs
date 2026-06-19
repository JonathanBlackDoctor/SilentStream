using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Converts between the persisted <see cref="AudioConfig"/> (config.json DTOs) and the runtime
/// <see cref="AudioSourceSettings"/>/<see cref="AudioFilterSettings"/> the mixer and encoder
/// consume. Source ids are derived deterministically so the UI, remote and config all address a
/// source by the same key across restarts.
/// </summary>
public static class AudioConfigMapper
{
    /// <summary>The fixed id of the single system-loopback source.</summary>
    public const string SystemSourceId = "system";

    /// <summary>Deterministic mixer id for a source: "system" or "mic:{deviceId|default}".</summary>
    public static string SourceId(string kind, string? deviceId) =>
        string.Equals(kind, "system", StringComparison.OrdinalIgnoreCase)
            ? SystemSourceId
            : "mic:" + (string.IsNullOrEmpty(deviceId) ? "default" : deviceId);

    /// <summary>Maps the persisted sources into runtime mixer settings (system source guaranteed).</summary>
    public static IReadOnlyList<AudioSourceSettings> ToSourceSettings(AudioConfig audio)
    {
        var result = new List<AudioSourceSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in audio.Sources)
        {
            var isSystem = string.Equals(s.Kind, "system", StringComparison.OrdinalIgnoreCase);
            var id = SourceId(s.Kind, s.DeviceId);
            if (!seen.Add(id))
            {
                continue; // ignore duplicate keys (e.g. two "default" mics)
            }

            result.Add(new AudioSourceSettings(
                Id: id,
                Kind: isSystem ? AudioSourceKind.System : AudioSourceKind.Microphone,
                DeviceId: isSystem ? null : s.DeviceId,
                Name: string.IsNullOrWhiteSpace(s.Name) ? (isSystem ? "시스템 소리" : "마이크") : s.Name,
                Gain: s.Gain,
                Muted: s.Muted,
                GateEnabled: s.GateEnabled,
                GateThresholdDb: s.GateThresholdDb));
        }

        // The mixer always needs the system loopback; synthesise it if config somehow lacks one.
        if (!seen.Contains(SystemSourceId))
        {
            result.Insert(0, new AudioSourceSettings(
                SystemSourceId, AudioSourceKind.System, null, "시스템 소리", audio.SystemVolume));
        }

        return result;
    }

    /// <summary>Maps the persisted master-filter settings into the encoder's filter spec.</summary>
    public static AudioFilterSettings ToFilterSettings(AudioFiltersConfig filters) =>
        new(
            NoiseSuppressionEnabled: filters.NoiseSuppressionEnabled,
            NoiseSuppressionDb: filters.NoiseSuppressionDb,
            CompressorEnabled: filters.CompressorEnabled,
            LimiterEnabled: filters.LimiterEnabled,
            MasterGainDb: filters.MasterGainDb);

    /// <summary>Serialises a runtime source back into its persisted form.</summary>
    public static AudioSourceConfig ToConfig(AudioSourceSettings s) =>
        new()
        {
            Kind = s.Kind == AudioSourceKind.System ? "system" : "mic",
            DeviceId = s.DeviceId,
            Name = s.Name,
            Gain = s.Gain,
            Muted = s.Muted,
            GateEnabled = s.GateEnabled,
            GateThresholdDb = s.GateThresholdDb
        };
}
