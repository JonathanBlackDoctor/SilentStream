namespace SilentStream.Core.Contracts;

/// <summary>
/// The session mp4 the encoder is currently writing, plus the precise local instant that maps
/// to position 0 in that file. The VOD cut needs this to translate a period's wall-clock window
/// into file-relative offsets (확장계획서 §4.1). This is an additive read-only view over the
/// existing recording bookkeeping — <see cref="IRecordingManager"/> is left unchanged.
/// </summary>
public interface IRecordingSessionInfo
{
    /// <summary>The active recording session, or null when nothing is being recorded.</summary>
    RecordingSession? Current { get; }

    /// <summary>
    /// Returns every recording part that overlaps the requested wall-clock window. The default
    /// keeps older implementations source-compatible while recording managers can expose history.
    /// </summary>
    IReadOnlyList<RecordingSegment> GetSegments(DateTime startLocal, DateTime endLocal)
    {
        var current = Current;
        return current is null || endLocal <= current.StartLocal
            ? []
            : [new RecordingSegment(current.FilePath, current.StartLocal, null)];
    }
}

/// <summary>
/// A recording session: the file being written and the local time recording started, which
/// corresponds to file offset 0 for the lossless cut.
/// </summary>
/// <param name="FilePath">Absolute path of the session mp4.</param>
/// <param name="StartLocal">Local timestamp mapped to file position 0.</param>
public sealed record RecordingSession(string FilePath, DateTime StartLocal);

/// <summary>A physical recording part and the wall-clock interval represented by that file.</summary>
public sealed record RecordingSegment(string FilePath, DateTime StartLocal, DateTime? EndLocal);
