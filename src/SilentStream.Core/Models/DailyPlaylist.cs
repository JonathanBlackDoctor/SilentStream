namespace SilentStream.Core.Models;

public sealed record DailyPlaylistRecord(
    string Room,
    DateOnly Date,
    string Title,
    string? PlaylistId,
    string? Url,
    string Status,
    DateTimeOffset? LastAttemptAtUtc,
    string? Error);

public static class DailyPlaylistStatus
{
    public const string Creating = "creating";
    public const string Ready = "ready";
    public const string AuthRequired = "auth-required";
    public const string Failed = "failed";
}
