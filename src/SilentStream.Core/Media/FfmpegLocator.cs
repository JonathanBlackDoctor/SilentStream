namespace SilentStream.Core.Media;

/// <summary>
/// Resolves the bundled FFmpeg binary: ffmpeg\ffmpeg.exe next to the app
/// (installer bundle, plan §3.11), falling back to "ffmpeg" on PATH for dev runs.
/// </summary>
public static class FfmpegLocator
{
    public static string Resolve()
    {
        var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", exeName);
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }
}
