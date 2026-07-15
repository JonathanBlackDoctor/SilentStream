using SilentStream.Core.Contracts;

namespace SilentStream.Core.Models;

/// <summary>Pending-split lifecycle states persisted in pending_splits.json.</summary>
public static class PendingSplitStatus
{
    /// <summary>Waiting for operator approval (or the auto-approve deadline).</summary>
    public const string Pending = "pending";

    /// <summary>Approved; the cut runs as soon as possible (or at ExecuteAtLocal when that is set).</summary>
    public const string Approved = "approved";

    /// <summary>Cut produced and enqueued for upload.</summary>
    public const string Done = "done";

    /// <summary>No VOD for this window: operator skip, or absorbed by the previous period's extension.</summary>
    public const string Skipped = "skipped";

    /// <summary>Cut failed (session file gone, ffmpeg error) — FailReason says why.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One approval-driven VOD cut (승인 기반 교시 분할). Created when a period boundary fires instead
/// of cutting immediately; the operator approves (optionally adjusting the window), merges it into
/// the next period (연강), or skips it. The recording-session snapshot is captured at creation —
/// <c>IRecordingSessionInfo.Current</c> is in-memory only, so a late approval must not re-read it
/// after the session ended, was replaced, or the app restarted.
/// </summary>
/// <param name="Id">Stable unique id.</param>
/// <param name="Date">The class date this window belongs to.</param>
/// <param name="Periods">Period numbers covered — one entry normally, several when merged (연강).</param>
/// <param name="StartLocal">Proposed cut start (scheduled, shifted by an adjusted previous boundary).</param>
/// <param name="EndLocal">Proposed cut end (the scheduled boundary).</param>
/// <param name="SessionFilePath">Session mp4 captured at creation.</param>
/// <param name="SessionStartLocal">Local instant mapping to position 0 of that file.</param>
/// <param name="Status">See <see cref="PendingSplitStatus"/>.</param>
/// <param name="CreatedLocal">When the split was created (the boundary fired).</param>
/// <param name="AutoApproveAtLocal">Auto-approve deadline; null = wait forever (manual only).</param>
/// <param name="ExecuteAtLocal">When an approved end lies in the future, the instant to cut at.</param>
/// <param name="ResolvedStartLocal">Start confirmed by the approval (null until approved).</param>
/// <param name="ResolvedEndLocal">End confirmed by the approval (null until approved).</param>
/// <param name="Title">Upload title, filled once the cut is enqueued (phone display).</param>
/// <param name="FailReason">Why the cut failed (phone display).</param>
public sealed record PendingSplit(
    string Id,
    DateOnly Date,
    IReadOnlyList<int> Periods,
    DateTime StartLocal,
    DateTime EndLocal,
    string SessionFilePath,
    DateTime SessionStartLocal,
    string Status,
    DateTime CreatedLocal,
    DateTime? AutoApproveAtLocal,
    DateTime? ExecuteAtLocal,
    DateTime? ResolvedStartLocal,
    DateTime? ResolvedEndLocal,
    string? Title,
    string? FailReason,
    IReadOnlyList<RecordingSegment>? RecordingSegments = null);

/// <summary>
/// An open 연강 (merge) chain: "don't cut yet — fold the next period's end into one VOD". Created
/// when the operator merges a pending split; closed (into a single merged <see cref="PendingSplit"/>)
/// by the next period-end boundary. <paramref name="FallbackEndLocal"/> preserves the pre-merge end
/// so a chain stranded by a crash or schedule change can still be materialised without losing the VOD.
/// </summary>
public sealed record MergeChain(
    DateOnly Date,
    IReadOnlyList<int> Periods,
    DateTime StartLocal,
    DateTime FallbackEndLocal,
    string SessionFilePath,
    DateTime SessionStartLocal);
