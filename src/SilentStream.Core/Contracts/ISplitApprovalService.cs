using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Approval-based period splitting (승인 기반 교시 분할): holds each period-end cut as a pending
/// split the operator approves (optionally adjusting the window), merges into the next period
/// (연강), or skips — from the phone remote. Untouched splits auto-approve at their default
/// boundary after <c>PeriodsConfig.AutoApproveMinutes</c>. State persists across restarts.
/// </summary>
public interface ISplitApprovalService
{
    /// <summary>
    /// Raised after any state change (creation, approval, merge, skip, auto-approve, cut done/failed)
    /// so the remote server can push fresh status to the phone.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>All splits: pending/approved plus recent terminal ones (phone history).</summary>
    IReadOnlyList<PendingSplit> Snapshot();

    /// <summary>The open 연강 chain, or null. Shown on the phone so a merge-in-progress is visible.</summary>
    MergeChain? OpenChain();

    /// <summary>Loads persisted state (recovering stranded work) and starts the timer loop. Idempotent.</summary>
    void Start(CancellationToken ct);

    /// <summary>
    /// Period-end entry point, routed here by <c>VodCoordinator</c> when approval mode is on.
    /// Creates a pending split (or closes an open 연강 chain into a merged one). No-op when
    /// nothing was being recorded during the window.
    /// </summary>
    void OnPeriodEnded(PeriodBoundary boundary);

    /// <summary>
    /// Approves a pending split, optionally overriding its window. An end in the future defers the
    /// cut until that instant. Adjusting the end also shifts the next contiguous period's start
    /// (the "boundary moved" semantic).
    /// </summary>
    SplitActionOutcome Approve(string id, DateTime? startLocal, DateTime? endLocal);

    /// <summary>Merges a pending split with the upcoming period (연강): opens a chain the next boundary closes.</summary>
    SplitActionOutcome Merge(string id);

    /// <summary>Reverts an open 연강 chain back to a pending split with its pre-merge window.</summary>
    SplitActionOutcome CancelMerge();

    /// <summary>Discards a pending split — no VOD for that window.</summary>
    SplitActionOutcome Skip(string id);
}

/// <summary>
/// Action result. <paramref name="Applied"/> is set by Approve: "cut" (runs now) or
/// "scheduled" (end is in the future; runs then). Errors are Korean, phone-displayable.
/// </summary>
public sealed record SplitActionOutcome(bool Ok, string? Error, string? Applied = null);
