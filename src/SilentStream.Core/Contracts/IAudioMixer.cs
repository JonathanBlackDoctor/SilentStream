using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Captures system audio (WASAPI loopback) plus zero or more microphones and mixes them into
/// a single PCM stream. Each source has independent gain (>1.0 amplifies), mute and an optional
/// noise gate; the mixer publishes real-time peak/RMS levels for the UI meters and warns when a
/// present microphone falls silent. A missing/failed source never stops the mix. See plan §3.4.
/// </summary>
public interface IAudioMixer : IDisposable
{
    /// <summary>Enumerates available microphone capture devices for the UI dropdown.</summary>
    IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices();

    /// <summary>
    /// Replaces the full set of sources (system + microphones). Safe to call before or while
    /// running; a running mixer rebuilds its capture graph to match. The system source, if
    /// omitted, is added automatically so desktop audio is always captured.
    /// </summary>
    void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources);

    /// <summary>The sources currently configured on the mixer.</summary>
    IReadOnlyList<AudioSourceSettings> Sources { get; }

    /// <summary>Real-time gain change for one source (linear; >1 amplifies). No rebuild.</summary>
    void SetGain(string sourceId, double gain);

    /// <summary>Real-time mute toggle for one source. No rebuild.</summary>
    void SetMuted(string sourceId, bool muted);

    /// <summary>Latest level snapshot (per source + master), safe to read from any thread.</summary>
    AudioLevels CurrentLevels { get; }

    /// <summary>Raised at ~15 Hz with fresh per-source + master levels for the meters.</summary>
    event EventHandler<AudioLevels> LevelsUpdated;

    /// <summary>Raised when a microphone's audible-signal presence changes (silence/restored).</summary>
    event EventHandler<MicSignalStatus> MicSignalChanged;

    /// <summary>Raised when a mixed PCM buffer is ready.</summary>
    event EventHandler<AudioBuffer> SamplesAvailable;

    /// <summary>Starts capturing and mixing audio.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops capture and releases audio devices.</summary>
    Task StopAsync();
}
