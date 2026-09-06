namespace SilentStream.Core.Models;

/// <summary>
/// Durable public link for a YouTube live broadcast. The link is kept separate from daily
/// playlists so a live broadcast is never accidentally exposed through the class playlist.
/// </summary>
public sealed record LiveReplayRecord(
    string BroadcastId,
    string Url,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);
