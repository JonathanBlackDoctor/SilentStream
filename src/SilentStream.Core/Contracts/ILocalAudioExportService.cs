namespace SilentStream.Core.Contracts;

/// <summary>
/// Creates a downloadable audio-only file from this application's own local period MP4.
/// This deliberately never reads audiovisual content back from YouTube.
/// </summary>
public interface ILocalAudioExportService
{
    /// <summary>
    /// Remuxes the first audio stream from <paramref name="sourceVideoPath"/> into an M4A file
    /// named for <paramref name="assetId"/>. Returns null when the source has no usable audio or
    /// FFmpeg cannot complete the export.
    /// </summary>
    Task<string?> ExportAsync(string sourceVideoPath, string assetId, CancellationToken ct);
}
