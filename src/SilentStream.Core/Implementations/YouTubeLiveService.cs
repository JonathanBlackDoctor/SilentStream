using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real OAuth2 + liveBroadcast/liveStream lifecycle lands in Phase 3.
/// </summary>
public sealed class YouTubeLiveService : IYouTubeLiveService
{
    public Task<bool> AuthenticateAsync(CancellationToken ct) =>
        throw new NotImplementedException("YouTubeLiveService.AuthenticateAsync — Phase 3.");

    public Task<LiveSession> CreateBroadcastAsync(CancellationToken ct) =>
        throw new NotImplementedException("YouTubeLiveService.CreateBroadcastAsync — Phase 3.");

    public Task CompleteBroadcastAsync(string broadcastId, CancellationToken ct) =>
        throw new NotImplementedException("YouTubeLiveService.CompleteBroadcastAsync — Phase 3.");
}
