namespace SilentStream.Core.Contracts;

/// <summary>Requires durable, validated upload receipts before deleting a source recording.</summary>
public interface IRecordingRetentionGuard
{
    bool CanDelete(RecordingSegment segment);
}
