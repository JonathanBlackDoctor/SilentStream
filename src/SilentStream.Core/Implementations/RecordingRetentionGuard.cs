using SilentStream.Core.Contracts;

namespace SilentStream.Core.Implementations;

public sealed class RecordingRetentionGuard(
    IPeriodAssetCatalog assets, IUploadQueue queue, IPeriodScheduleStore schedules) : IRecordingRetentionGuard
{
    public bool CanDelete(RecordingSegment segment)
    {
        if (segment.EndLocal is not { } end || end <= segment.StartLocal ||
            end - segment.StartLocal > TimeSpan.FromDays(2)) return false;
        var receipts = assets.Snapshot(); // Any unreadable receipt must stop deletion, not imply success.
        var jobs = queue.Snapshot();
        for (var date = DateOnly.FromDateTime(segment.StartLocal); date <= DateOnly.FromDateTime(end); date = date.AddDays(1))
        {
            foreach (var slot in schedules.ResolveForDate(date).Periods)
            {
                var start = date.ToDateTime(slot.Start);
                var finish = date.ToDateTime(slot.End);
                if (start >= end || finish <= segment.StartLocal) continue;
                var key = VodSegmentService.SourceKeyFor(segment, start, finish);
                if (jobs.Any(j => j.SourceKey == key &&
                    (j.Status != UploadJobStatus.Completed || j.NeedsUploadReconciliation))) return false;
                if (!receipts.Any(a => a.SourceKey == key && !string.IsNullOrWhiteSpace(a.VideoId) &&
                    a.UploadValidationVersion >= 1 && a.UploadedFileSizeBytes >= 2048)) return false;
            }
        }
        return true;
    }
}
