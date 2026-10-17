using SilentStream.Core.Implementations;
using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>Per-input retry budget. Opening a capture is not proof that sound has recovered.</summary>
public sealed class MicrophoneRecoveryPolicy(DateTime startedAtUtc)
{
    private DateTime _nextSilentRetry = startedAtUtc.AddSeconds(60);
    private DateTime _nextFailureRetry = startedAtUtc;
    private int _silentAttempts;
    private int _failureAttempts;

    public bool CanRetryFailure(DateTime now) => now >= _nextFailureRetry;
    public bool CanRetrySilence(DateTime now, AudioSourceSettings source, bool loopback) =>
        !loopback && !source.Muted && source.Gain > 0 && now >= _nextSilentRetry;

    public void ObserveSignal(DateTime now)
    {
        _silentAttempts = _failureAttempts = 0;
        _nextSilentRetry = now.AddSeconds(60);
        _nextFailureRetry = now;
    }

    public void Attempt(DateTime now, bool silent)
    {
        int[] failureDelays = [30, 60, 120, 300];
        _nextFailureRetry = now.AddSeconds(failureDelays[Math.Min(_failureAttempts++, failureDelays.Length - 1)]);
        if (silent)
        {
            int[] delays = [300, 600];
            _nextSilentRetry = now.AddSeconds(delays[Math.Min(_silentAttempts++, delays.Length - 1)]);
        }
        else
        {
            // Give a reopened stream time to produce audible samples.
            if (_nextSilentRetry < now.AddSeconds(60)) _nextSilentRetry = now.AddSeconds(60);
        }
    }

    /// <summary>Exactly one source per active non-loopback endpoint; never retain a default alias too.</summary>
    public static IReadOnlyList<AudioSourceSettings> ConnectedSources(
        IReadOnlyList<AudioSourceSettings> known, IReadOnlyList<AudioDeviceInfo> devices)
    {
        var result = known.Where(s => s.Kind == AudioSourceKind.System).Take(1).ToList();
        var fallback = known.FirstOrDefault(s => s.Kind == AudioSourceKind.Microphone && string.IsNullOrEmpty(s.DeviceId));
        foreach (var device in devices.Where(d => !d.IsLoopback && !AudioDeviceClassifier.IsLoopbackName(d.Name))
                     .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            var previous = known.FirstOrDefault(s => s.Kind == AudioSourceKind.Microphone
                && string.Equals(s.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(previous ?? (fallback ?? new AudioSourceSettings("", AudioSourceKind.Microphone, null, "")) with
            {
                Id = AudioConfigMapper.SourceId("mic", device.Id), DeviceId = device.Id, Name = device.Name
            });
        }
        if (result.All(s => s.Kind != AudioSourceKind.Microphone))
            result.Add(fallback ?? new("mic:default", AudioSourceKind.Microphone, null, "마이크 (연결 대기)"));
        return result;
    }
}
