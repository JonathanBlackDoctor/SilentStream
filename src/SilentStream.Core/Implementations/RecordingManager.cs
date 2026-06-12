using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real session-file naming, capacity cap and 7-day retention land in Phase 2.
/// </summary>
public sealed class RecordingManager : IRecordingManager
{
    public string CreateSessionFilePath(DateTime sessionStartUtc) =>
        throw new NotImplementedException("RecordingManager.CreateSessionFilePath — Phase 2.");

    public RecordingStatus GetStatus() => RecordingStatus.Empty;

    public Task EnforceRetentionAsync(CancellationToken ct) =>
        throw new NotImplementedException("RecordingManager.EnforceRetentionAsync — Phase 2.");
}
