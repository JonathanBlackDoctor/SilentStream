using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>Crash-safe history of public live/replay links; never stores RTMP ingest secrets.</summary>
public interface ILiveReplayCatalog
{
    LiveReplayRecord? Latest { get; }

    IReadOnlyList<LiveReplayRecord> Snapshot();

    void RecordStarted(LiveSession session, DateTimeOffset startedAtUtc);

    void RecordEnded(string broadcastId, DateTimeOffset endedAtUtc);
}
