using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Mixes system audio (WASAPI loopback) and a selectable microphone into a single PCM
/// stream with independent volumes. Survives mic disconnect (system audio only). See plan §3.4.
/// </summary>
public interface IAudioMixer : IDisposable
{
    /// <summary>System (loopback) volume, 0.0-1.0.</summary>
    double SystemVolume { get; set; }

    /// <summary>Microphone volume, 0.0-1.0.</summary>
    double MicVolume { get; set; }

    /// <summary>Selected microphone device id, null = system default.</summary>
    string? MicDeviceId { get; set; }

    /// <summary>Enumerates available microphone capture devices for the UI dropdown.</summary>
    IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices();

    /// <summary>Raised when a mixed PCM buffer is ready.</summary>
    event EventHandler<AudioBuffer> SamplesAvailable;

    /// <summary>Starts capturing and mixing audio.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops capture and releases audio devices.</summary>
    Task StopAsync();
}
