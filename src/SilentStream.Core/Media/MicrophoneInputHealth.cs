using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>Transport evidence and audible signal are independent; silence never proves a bad cable.</summary>
public sealed record MicrophoneInputState(string Code, string Message, bool IsFailure = false);

public static class MicrophoneInputHealth
{
    public static readonly TimeSpan PacketGrace = TimeSpan.FromSeconds(5);

    public static MicrophoneInputState Evaluate(AudioSourceSettings source, AudioSourceRuntimeStatus? status,
        bool signalPresent, DateTime now)
    {
        if (source.Kind != AudioSourceKind.Microphone || status?.IsLoopback == true
            || AudioDeviceClassifier.IsLoopbackName(source.Name))
            return new("loopback", "시스템 오디오 입력");
        if (status is null || !status.CaptureActive && status.Failure == AudioCaptureFailure.None
            && string.IsNullOrWhiteSpace(status.Error))
            return new("inactive", "입력 시작 전 · 연결 상태 미확인");
        if (!status.CaptureActive)
            return status.Failure switch
            {
                AudioCaptureFailure.DeviceUnavailable => new("deviceUnavailable", "장치 인식 안 됨 · USB·전원·Windows 입력 장치를 확인하세요.", true),
                AudioCaptureFailure.DataStopped => new("dataStopped", "입력 데이터 중단 · 연결·드라이버를 확인하세요. 원인은 아직 확정되지 않았습니다.", true),
                _ => new("captureError", "마이크 캡처 오류 · 장치·권한·드라이버를 확인하세요.", true)
            };
        if (now - (status.LastPacketAtUtc ?? status.UpdatedAtUtc) > PacketGrace)
            return new("dataStopped", "입력 데이터 중단 · 연결·드라이버를 확인하세요. 원인은 아직 확정되지 않았습니다.", true);
        if (source.Muted || source.Gain <= 0)
            return new("muted", "앱 음소거 또는 입력 게인 0");
        if (status.LastPacketAtUtc is null)
            return new("starting", "장치 열림 · 첫 입력 데이터 대기 중");
        return signalPresent
            ? new("signal", "연결됨 · 소리 입력 중")
            : new("quiet", "연결됨 · 입력 데이터 정상 · 현재 조용함");
    }
}
