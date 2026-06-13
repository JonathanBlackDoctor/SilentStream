using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real WASAPI loopback + microphone mixing (NAudio) lands in Phase 2.
/// </summary>
public sealed class AudioMixer : IAudioMixer
{
    public double SystemVolume { get; set; } = 1.0;
    public double MicVolume { get; set; } = 1.0;
    public string? MicDeviceId { get; set; }

#pragma warning disable CS0067 // part of the fixed contract; implemented in Phase 2
    public event EventHandler<AudioBuffer>? SamplesAvailable;
#pragma warning restore CS0067

    public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => Array.Empty<AudioDeviceInfo>();

    public Task StartAsync(CancellationToken ct) =>
        throw new NotImplementedException("AudioMixer.StartAsync — Phase 2 (NAudio).");

    public Task StopAsync() =>
        throw new NotImplementedException("AudioMixer.StopAsync — Phase 2 (NAudio).");

    public void Dispose() { }
}
