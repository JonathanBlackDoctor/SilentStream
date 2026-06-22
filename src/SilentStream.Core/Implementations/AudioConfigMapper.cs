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

    /// <summary>
    /// First-run seeding (schema v4): rebuilds <see cref="AudioConfig.Sources"/> as the system
    /// loopback plus one source per real (non-loopback) microphone currently present, so a fresh
    /// install captures every connected mic — instead of a single default device that may be the
    /// wrong/silent one (2nd field-test root cause: the default "communications" mic carried no
    /// signal while the voice was on another device). Loopback monitors (Stereo Mix / What U Hear)
    /// are excluded, since adding one as a "mic" would just double the system audio. The per-source
    /// noise gate is enabled only when there are two or more mics, so idle extra mics add no hiss to
    /// the mix while a lone voice mic is never at risk of being gated out. When no real mic is found
    /// the layout falls back to a single default-device mic (never mic-less). No-op once
    /// <see cref="AudioConfig.MicsSeeded"/> is set. Returns true when it changed the config.
    /// </summary>
    public static bool SeedMicrophoneSources(AudioConfig audio, IReadOnlyList<AudioDeviceInfo> micDevices)
    {
        if (audio.MicsSeeded)
        {
            return false;
        }
        audio.MicsSeeded = true;

        // Preserve the existing system source (its gain/mute) if one is already configured.
        var system = audio.Sources.FirstOrDefault(
                         s => string.Equals(s.Kind, "system", StringComparison.OrdinalIgnoreCase))
                     ?? new AudioSourceConfig { Kind = "system", DeviceId = null, Name = "시스템 소리" };

        var realMics = micDevices.Where(d => !d.IsLoopback).ToList();
        var sources = new List<AudioSourceConfig> { system };

        if (realMics.Count == 0)
        {
            // No real microphone present: keep behaviour no worse than before — one default-device
            // mic (DeviceId null → the mixer resolves the system default capture endpoint).
            sources.Add(new AudioSourceConfig { Kind = "mic", DeviceId = null, Name = "마이크" });
        }
        else
        {
            var gateIdleMics = realMics.Count >= 2;
            foreach (var device in realMics)
            {
                sources.Add(new AudioSourceConfig
                {
                    Kind = "mic",
                    DeviceId = device.Id,
                    Name = string.IsNullOrWhiteSpace(device.Name) ? "마이크" : device.Name,
                    GateEnabled = gateIdleMics
                });
            }
        }

        audio.Sources = sources;
        return true;
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
