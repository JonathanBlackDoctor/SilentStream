namespace SilentStream.Core.Contracts;

/// <summary>
/// Supplies a low-rate, downscaled JPEG snapshot of the captured video so the operator can
/// confirm the correct screen is being sent — in the control window and on the phone remote
/// (OBS 대비 송출 미리보기). Taps the capture frames; produces nothing until capture is running.
/// </summary>
public interface IPreviewProvider
{
    /// <summary>The most recent preview frame as JPEG bytes, or null if none has been produced yet.</summary>
    byte[]? GetLatestJpegFrame();

    /// <summary>Raised (~2 fps) when a fresh preview frame is available.</summary>
    event Action FrameUpdated;
}
