namespace SilentStream.Core.Contracts;

/// <summary>
/// Creates a downloadable audio-only file from a trusted media file that already exists locally.
/// Acquiring that local source, including an optional YouTube recovery download, is the caller's
/// responsibility.
/// </summary>
public interface ILocalAudioExportService
{
    /// <summary>
    /// Re-encodes the first audio stream from <paramref name="sourceMediaPath"/> into an M4A file
    /// named for <paramref name="assetId"/>. Returns null when the source has no usable audio or
    /// FFmpeg cannot complete the export.
    /// </summary>
    Task<string?> ExportAsync(string sourceMediaPath, string assetId, CancellationToken ct);
}
