using SilentStream.Core.Contracts;

namespace SilentStream.Core.Remote;

/// <summary>Bounded, path-free upload status. Estimates use measured transfers, never stream bitrate.</summary>
public static class UploadQueueRemote
{
    public static UploadQueueView Build(IReadOnlyList<UploadJob> jobs, DateTimeOffset now,
        DateTimeOffset? pausedUntilUtc = null)
    {
        var measured = jobs.Where(j => j.Status == UploadJobStatus.Completed &&
                j.UploadDurationSeconds is > 0 && double.IsFinite(j.UploadDurationSeconds.Value))
            .TakeLast(20).ToArray();
        var average = measured.Length == 0 ? (double?)null : measured.Average(j => j.UploadDurationSeconds!.Value);
        var sized = measured.Where(j => j.FileSizeBytes is > 0).ToArray();
        var secondsPerByte = sized.Length < 3 ? (double?)null :
            sized.Sum(j => j.UploadDurationSeconds!.Value) / sized.Sum(j => (double)j.FileSizeBytes!.Value);
        // The worker consumes queue insertion order. Include ALL active jobs when estimating,
        // even when the UI only retains a bounded set of terminal history.
        DateTimeOffset? cursor = pausedUntilUtc > now ? pausedUntilUtc : now;
        var estimates = new Dictionary<string, DateTimeOffset?>();
        foreach (var job in jobs.Where(j => j.Status == UploadJobStatus.Uploading)
                     .Concat(jobs.Where(j => j.Status == UploadJobStatus.Pending)))
        {
            var seconds = secondsPerByte is { } rate && job.FileSizeBytes is > 0
                ? rate * job.FileSizeBytes.Value : measured.Length >= 3 ? average : null;
            if (job.Attempts > 0 || seconds is null || !double.IsFinite(seconds.Value))
                cursor = null; // retries have unknown causes and delay everything behind them
            if (cursor is { } start && seconds is { } duration)
            {
                var elapsed = job.Status == UploadJobStatus.Uploading && job.UploadStartedAtUtc is { } began
                    ? Math.Max(0, (now - began).TotalSeconds) : 0;
                if (elapsed >= duration || duration > 7 * 86400)
                    cursor = null; // do not keep moving an already-missed estimate into the future
                else
                    cursor = start.AddSeconds(duration - elapsed);
            }
            estimates[job.Id] = cursor;
        }
        var retained = jobs.Where(j => j.Status is UploadJobStatus.Pending or UploadJobStatus.Uploading)
            .Take(200)
            .Concat(jobs.Where(j => j.Status is not (UploadJobStatus.Pending or UploadJobStatus.Uploading))
                .TakeLast(100)).ToArray();
        return new(jobs.Count(j => j.Status == UploadJobStatus.Pending),
            jobs.Count(j => j.Status == UploadJobStatus.Uploading),
            jobs.Count(j => j.Status == UploadJobStatus.Completed),
            jobs.Count(j => j.Status == UploadJobStatus.Failed),
            measured.Length, average, pausedUntilUtc > now ? pausedUntilUtc : null,
            retained.Select(j => new UploadItemView(j.Id, j.Date.ToString("yyyy-MM-dd"),
                j.PeriodNumber, j.ResolvedSlotId, j.ResolvedSlotLabel, j.ResolvedSlotOrder,
                j.Title, j.Status, j.Attempts, j.VideoId, j.FileSizeBytes,
                j.UploadStartedAtUtc, j.UploadCompletedAtUtc, j.UploadDurationSeconds,
                j.Status == UploadJobStatus.Uploading && j.UploadStartedAtUtc is { } began
                    ? Math.Max(0, (now - began).TotalSeconds) : null,
                estimates.GetValueOrDefault(j.Id))).ToArray(), jobs.Count - retained.Length);
    }
}

public sealed record UploadQueueView(int Pending, int Uploading, int Completed, int Failed,
    int MeasuredCount, double? AverageDurationSeconds, DateTimeOffset? PausedUntilUtc,
    IReadOnlyList<UploadItemView> Items, int OmittedItems);

public sealed record UploadItemView(string Id, string Date, int Period, string SlotId,
    string SlotLabel, int SlotOrder, string Title, string Status, int Attempts, string? VideoId,
    long? FileSizeBytes, DateTimeOffset? UploadStartedAtUtc, DateTimeOffset? UploadCompletedAtUtc,
    double? UploadDurationSeconds, double? UploadElapsedSeconds, DateTimeOffset? EstimatedCompletedAtUtc);
