using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Cuts a single school period out of the in-progress session recording, losslessly
/// (FFmpeg <c>-c copy</c>, no re-encode), producing a standalone mp4 for upload (확장계획서 §4.1).
/// Read-only with respect to the live/tee pipeline — it only reads the session file.
/// </summary>
public interface IVodSegmentService
{
    /// <summary>
    /// Extracts <paramref name="period"/>'s [start, end] window from the current session mp4.
    /// Returns the generated file path, or null when there is no session, the window is empty,
    /// or the cut fails (best-effort; the app must not crash — 확장계획서 AC E2.3).
    /// </summary>
    Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct);

    /// <summary>
    /// Extracts an arbitrary [start, end] wall-clock window from an explicit session snapshot —
    /// the approval-based split path (adjusted boundaries, merged 연강 periods, cuts executed
    /// after the session ended or the app restarted). <paramref name="fileBaseLabel"/> names the
    /// output file ("1교시", "1~2교시"). Same best-effort null semantics as ExtractPeriodAsync.
    /// </summary>
    Task<string?> ExtractRangeAsync(
        RecordingSession session, DateTime startLocal, DateTime endLocal, string fileBaseLabel,
        CancellationToken ct);
}
