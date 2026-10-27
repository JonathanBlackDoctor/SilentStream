using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

public interface IYouTubePlaylistService
{
    Task<DailyPlaylistRecord> EnsurePlaylistAsync(string room, DateOnly date, CancellationToken ct);

    Task AddVideoAsync(string room, DateOnly date, string videoId, CancellationToken ct);

    IReadOnlyList<DailyPlaylistRecord> Snapshot(string room, DateOnly from, DateOnly to);

    bool HasRetryableFailures { get; }

    bool IsVideoAdded(string room, DateOnly date, string videoId) => false;
}
public interface IYouTubePlaylistGateway
{
    Task<string?> FindPlaylistByMarkerAsync(string marker, CancellationToken ct);

    Task<string> CreatePlaylistAsync(string title, string description, CancellationToken ct);

    Task<bool> ContainsVideoAsync(string playlistId, string videoId, CancellationToken ct);

    Task AddVideoAsync(string playlistId, string videoId, CancellationToken ct);
}

public sealed class YouTubeAuthorizationRequiredException : InvalidOperationException
{
    public YouTubeAuthorizationRequiredException(string message) : base(message) { }

    public YouTubeAuthorizationRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}
