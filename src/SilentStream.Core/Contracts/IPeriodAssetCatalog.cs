using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Persistent index of the downloadable assets created for each class-period video.
/// Implementations return immutable snapshots ordered newest-update first.
/// </summary>
public interface IPeriodAssetCatalog
{
    /// <summary>
    /// Adds a new asset or replaces the asset with the same stable id. The catalog stamps the
    /// returned asset's <see cref="PeriodAsset.UpdatedAtUtc"/> with its current UTC time.
    /// </summary>
    PeriodAsset Upsert(PeriodAsset asset);

    /// <summary>
    /// Returns a detached, deterministic newest-first snapshot. Ordering is by UpdatedAtUtc,
    /// then class date, period number, and ordinal id.
    /// </summary>
    IReadOnlyList<PeriodAsset> Snapshot();

    /// <summary>Finds one asset by its stable id, or returns null when it is absent.</summary>
    PeriodAsset? Find(string id);

    /// <summary>
    /// Records the YouTube id returned for an uploaded VOD. Returns false when no asset has the id.
    /// </summary>
    bool MarkUploaded(string id, string videoId);

    /// <summary>
    /// Records the current caption-processing state and its optional language/status message.
    /// Returns false when no asset has the id.
    /// </summary>
    bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null);
}
