namespace SilentStream.Core.Media;

/// <summary>
/// Resolves the pinned yt-dlp binary bundled by the Velopack release, with a PATH fallback for
/// development runs.
/// </summary>
public static class YtDlpLocator
{
    public static string Resolve() => Resolve(AppContext.BaseDirectory);

    /// <summary>Test seam for resolving from a supplied application base directory.</summary>
    public static string Resolve(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var exeName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        var bundled = Path.Combine(baseDirectory, "yt-dlp", exeName);
        return File.Exists(bundled) ? bundled : exeName;
    }
}
