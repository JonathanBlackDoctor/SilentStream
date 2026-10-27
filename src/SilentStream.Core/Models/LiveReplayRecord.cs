namespace SilentStream.Core.Models;

/// <summary>
/// Durable public link for a YouTube live broadcast. The link is kept separate from daily
/// playlists until the school day and YouTube broadcast have both ended.
/// </summary>
public sealed record LiveReplayRecord(
    string BroadcastId,
    string Url,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? Room = null,
    DateTimeOffset? PlaylistAddedAtUtc = null);
