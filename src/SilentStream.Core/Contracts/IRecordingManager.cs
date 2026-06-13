using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Owns the local backup recording: per-session file naming, capacity cap, and the
/// 7-day retention/disk-space cleanup. Operates independently of streaming. See plan §3.6.
/// </summary>
public interface IRecordingManager
{
    /// <summary>
    /// Builds the session file path (e.g. SilentStream_REC_2026-06-12_0930.mp4) inside the
    /// configured folder, based on the local session start time (plan §3.6 file naming).
    /// </summary>
    string CreateSessionFilePath(DateTime sessionStartLocal);

    /// <summary>Returns the current recording status (active file, used/free bytes).</summary>
    RecordingStatus GetStatus();

    /// <summary>
    /// Enforces retention: deletes files past the retention window, then oldest-first until
    /// under the capacity cap / above the min-free-space threshold.
    /// </summary>
    Task EnforceRetentionAsync(CancellationToken ct);
}
