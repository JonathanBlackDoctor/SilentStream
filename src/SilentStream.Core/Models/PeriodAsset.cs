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
    DateTime UpdatedAtUtc = default,
    string SlotId = "",
    string SlotLabel = "",
    string SlotKind = ScheduleSlotKinds.Period,
    int SlotOrder = 0)
{
    public string ResolvedSlotId => !string.IsNullOrWhiteSpace(SlotId)
        ? SlotId
        : $"period-{PeriodNumber}";
    public string ResolvedSlotLabel => !string.IsNullOrWhiteSpace(SlotLabel)
        ? SlotLabel
        : $"{PeriodNumber}교시";
    public string ResolvedSlotKind => string.Equals(SlotKind, ScheduleSlotKinds.Lunch,
        StringComparison.OrdinalIgnoreCase) ? ScheduleSlotKinds.Lunch : ScheduleSlotKinds.Period;
    public int ResolvedSlotOrder => SlotOrder > 0 ? SlotOrder : PeriodNumber;
}

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
