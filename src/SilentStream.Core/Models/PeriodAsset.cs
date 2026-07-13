namespace SilentStream.Core.Models;

/// <summary>
/// Durable metadata for the downloadable artifacts belonging to one school-period VOD.
/// The catalog owns <see cref="UpdatedAtUtc"/> and refreshes it on every mutation.
/// </summary>
public sealed record PeriodAsset(
    string Id,
    DateOnly Date,
    int PeriodNumber,
    string Title,
    string? AudioPath = null,
    string? VideoId = null,
    string CaptionStatus = PeriodAssetCaptionStatus.Pending,
    string? CaptionLanguage = null,
    string? CaptionMessage = null,
    DateTime UpdatedAtUtc = default);

/// <summary>Known caption-download states. The catalog also permits future status values.</summary>
public static class PeriodAssetCaptionStatus
{
    /// <summary>A caption track has not yet been fetched.</summary>
    public const string Pending = "pending";

    /// <summary>The service is currently checking or downloading the caption track.</summary>
    public const string Downloading = "downloading";

    /// <summary>A downloadable caption/transcript was successfully fetched on demand.</summary>
    public const string Available = "available";

    /// <summary>No usable caption track is currently available for the video.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>A caption lookup or download attempt failed.</summary>
    public const string Failed = "failed";
}
