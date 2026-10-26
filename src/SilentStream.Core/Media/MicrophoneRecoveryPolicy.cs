using SilentStream.Core.Implementations;
using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>Per-input capture-failure retry budget. Valid quiet buffers never trigger a reopen.</summary>
public sealed class MicrophoneRecoveryPolicy(DateTime startedAtUtc)
{
    private DateTime _nextFailureRetry = startedAtUtc;
    private int _failureAttempts;

    public bool CanRetryFailure(DateTime now) => now >= _nextFailureRetry;

    public void ObserveSignal(DateTime now)
    {
        _failureAttempts = 0;
        _nextFailureRetry = now;
    }

    public void Attempt(DateTime now)
    {
        int[] delays = [30, 60, 120, 300];
        _nextFailureRetry = now.AddSeconds(delays[Math.Min(_failureAttempts++, delays.Length - 1)]);
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
