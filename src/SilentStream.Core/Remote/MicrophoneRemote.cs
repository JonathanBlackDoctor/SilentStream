using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.Media;

namespace SilentStream.Core.Remote;

public static class MicrophoneRemote
{
    public const string AllConnectedDeviceId = "*";
    // An explicit operator selection uses one microphone, avoiding duplicate classroom audio.
    // System audio and its settings are retained. Device selection does not remove mute/gate.
    public static IReadOnlyList<AudioSourceSettings> Select(
        IReadOnlyList<AudioSourceSettings> sources, IReadOnlyList<AudioDeviceInfo> devices, string deviceId)
    {
        if (deviceId == AllConnectedDeviceId)
            return MicrophoneRecoveryPolicy.ConnectedSources(sources, devices);
        var device = devices.FirstOrDefault(d => d.Id == deviceId && !d.IsLoopback)
            ?? throw new ArgumentException("선택한 마이크가 연결되어 있지 않습니다. 목록을 새로고침하세요.");
        var existing = sources.FirstOrDefault(s => s.Kind == AudioSourceKind.Microphone && s.DeviceId == deviceId);
        var selected = existing ?? new AudioSourceSettings(
            AudioConfigMapper.SourceId("mic", device.Id), AudioSourceKind.Microphone, device.Id, device.Name);
        return sources.Where(s => s.Kind == AudioSourceKind.System).Append(selected).ToArray();
    }

    public static IReadOnlyList<AudioSourceSettings> RestoreInput(IReadOnlyList<AudioSourceSettings> sources) =>
        sources.Select(s => s.Kind == AudioSourceKind.Microphone
            ? s with { Muted = false, Gain = s.Gain <= 0 ? 1 : s.Gain, GateEnabled = false }
            : s).ToArray();
}
