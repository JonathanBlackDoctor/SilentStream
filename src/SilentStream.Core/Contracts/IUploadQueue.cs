namespace SilentStream.Core.Contracts;

/// <summary>
/// Persistent, quota-aware upload queue (확장계획서 §4.2). Jobs survive reboots (JSON on disk),
/// the worker uploads oldest-first until YouTube reports quota exhaustion, resuming after the
/// Pacific-time reset so 7교시+ overflow uploads the next day (D7).
/// </summary>
public interface IUploadQueue
{
    /// <summary>Adds a job and persists it immediately, then nudges the worker.</summary>
    void Enqueue(UploadJob job);

    /// <summary>Current snapshot of all jobs (pending, uploading, completed, failed).</summary>
    IReadOnlyList<UploadJob> Snapshot();

    /// <summary>Known quota pause, if any. Does not start or mutate the worker.</summary>
    DateTimeOffset? PausedUntilUtc => null;

    /// <summary>Starts the background worker. Idempotent; stops on <paramref name="ct"/>.</summary>
    void Start(CancellationToken ct);
}

/// <summary>
/// One queued upload (확장계획서 §5). Immutable; the queue replaces entries with <c>with</c>
/// expressions as status/attempts/videoId change.
/// </summary>
/// <param name="Id">Stable unique id.</param>
/// <param name="FilePath">Absolute path of the cut mp4 to upload.</param>
/// <param name="Title">Upload title (e.g. "1교시 - 2026-06-14").</param>
/// <param name="Date">The class date this period belongs to.</param>
/// <param name="PeriodNumber">1-based period number.</param>
/// <param name="Status">See <see cref="UploadJobStatus"/>.</param>
/// <param name="Attempts">Number of failed upload attempts so far.</param>
/// <param name="VideoId">Resulting YouTube video id once completed, else null.</param>
/// <param name="UploadStartedAtUtc">Start of the latest upload attempt.</param>
/// <param name="UploadCompletedAtUtc">Successful completion time, if uploaded.</param>
/// <param name="UploadDurationSeconds">Measured duration of the successful upload attempt.</param>
/// <param name="FileSizeBytes">Cut-file size captured before successful uploads delete it.</param>
public sealed record UploadJob(
    string Id,
    string FilePath,
    string Title,
    DateOnly Date,
    int PeriodNumber,
    string Status,
    int Attempts,
    string? VideoId,
    string SlotId = "",
    string SlotLabel = "",
    string SlotKind = Models.ScheduleSlotKinds.Period,
    int SlotOrder = 0,
    DateTimeOffset? UploadStartedAtUtc = null,
    DateTimeOffset? UploadCompletedAtUtc = null,
    double? UploadDurationSeconds = null,
    long? FileSizeBytes = null,
    string? SourceKey = null)
{
    public string ResolvedSlotId => !string.IsNullOrWhiteSpace(SlotId)
        ? SlotId
        : $"period-{PeriodNumber}";
    public string ResolvedSlotLabel => !string.IsNullOrWhiteSpace(SlotLabel)
        ? SlotLabel
        : $"{PeriodNumber}교시";
    public string ResolvedSlotKind => string.Equals(SlotKind, Models.ScheduleSlotKinds.Lunch,
        StringComparison.OrdinalIgnoreCase) ? Models.ScheduleSlotKinds.Lunch : Models.ScheduleSlotKinds.Period;
    public int ResolvedSlotOrder => SlotOrder > 0 ? SlotOrder : PeriodNumber;
}

/// <summary>Upload job lifecycle states persisted in the queue file.</summary>
public static class UploadJobStatus
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
