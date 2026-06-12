using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// YouTube Data API v3 integration: installed-app OAuth2, refresh-token rotation, and
/// liveBroadcast/liveStream create-bind-complete lifecycle. See plan §3.7.
/// </summary>
public interface IYouTubeLiveService
{
    /// <summary>
    /// Ensures a valid access token (interactive browser login on first run, silent
    /// refresh afterwards). Returns true on success.
    /// </summary>
    Task<bool> AuthenticateAsync(CancellationToken ct);

    /// <summary>
    /// Creates a new unlisted broadcast, binds a stream, and returns its ingest details.
    /// </summary>
    Task<LiveSession> CreateBroadcastAsync(CancellationToken ct);

    /// <summary>Transitions the broadcast to the 'complete' (ended) state.</summary>
    Task CompleteBroadcastAsync(string broadcastId, CancellationToken ct);
}
