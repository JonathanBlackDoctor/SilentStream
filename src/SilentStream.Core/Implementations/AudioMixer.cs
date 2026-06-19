using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 / non-Windows stub. The real WASAPI loopback + multi-microphone mixing (NAudio)
/// lives in <c>SilentStream.Media.Windows.WasapiAudioMixer</c>, which the WPF host swaps in.
/// </summary>
public sealed class AudioMixer : IAudioMixer
{
    private IReadOnlyList<AudioSourceSettings> _sources = Array.Empty<AudioSourceSettings>();

    public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => Array.Empty<AudioDeviceInfo>();

    public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources) => _sources = sources;

    public IReadOnlyList<AudioSourceSettings> Sources => _sources;

    public void SetGain(string sourceId, double gain) { }

    public void SetMuted(string sourceId, bool muted) { }

    public AudioLevels CurrentLevels => AudioLevels.Empty;

#pragma warning disable CS0067 // part of the fixed contract; implemented in the Windows mixer
    public event EventHandler<AudioLevels>? LevelsUpdated;
    public event EventHandler<MicSignalStatus>? MicSignalChanged;
    public event EventHandler<AudioBuffer>? SamplesAvailable;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) =>
        throw new NotImplementedException("AudioMixer.StartAsync — Windows mixer (NAudio).");

    public Task StopAsync() =>
        throw new NotImplementedException("AudioMixer.StopAsync — Windows mixer (NAudio).");

    public void Dispose() { }
}
