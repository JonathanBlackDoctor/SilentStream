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
    /// Length in bytes of the file the encoder is currently writing (0 when recording is idle,
    /// disabled, or the file does not exist yet). The watchdog samples this as a second, independent
    /// liveness signal: a growing file proves the encoder is still muxing frames even when ffmpeg's
    /// progress line has gone quiet (a static screen + silent audio starve the stats cadence), so a
    /// healthy-but-quiet pipeline is not churned into a restart loop (plan §4.4).
    /// </summary>
    long GetActiveRecordingLength();

    /// <summary>
    /// Enforces retention: deletes files past the retention window, then oldest-first until
    /// under the capacity cap / above the min-free-space threshold.
    /// </summary>
    Task EnforceRetentionAsync(CancellationToken ct);
}
