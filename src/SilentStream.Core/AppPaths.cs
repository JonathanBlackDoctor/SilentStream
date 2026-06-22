namespace SilentStream.Core;

/// <summary>
/// Well-known filesystem locations (plan §3.9 / §3.10). All app state lives under
/// %AppData%\MediaCaptureHelper; recordings default to %USERPROFILE%\Videos\MediaCaptureHelper.
/// </summary>
public static class AppPaths
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaCaptureHelper");

    public static string LogsDir => Path.Combine(AppDataDir, "logs");

    public static string ConfigFile => Path.Combine(AppDataDir, "config.json");

    /// <summary>OAuth client credentials file (never committed — see docs/CLAUDE.local.md.template).</summary>
    public static string ClientSecretFile => Path.Combine(AppDataDir, "client_secret.json");

    /// <summary>Scratch folder for per-period VOD cuts awaiting upload (확장계획서 §4.1).</summary>
    public static string VodDir => Path.Combine(AppDataDir, "vod");

    /// <summary>Persistent, quota-aware upload queue (확장계획서 §4.2). Not sensitive.</summary>
    public static string UploadQueueFile => Path.Combine(AppDataDir, "upload_queue.json");

    public static string DefaultRecordingFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
            {
                videos = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }
            return Path.Combine(videos, "MediaCaptureHelper");
        }
    }

    /// <summary>
    /// One-time rename of the legacy %AppData%\SilentStream folder to the current
    /// %AppData%\MediaCaptureHelper, so an existing install's login/config/queue carry over
    /// after the rebrand. No-op when the legacy folder is absent or the new one already exists;
    /// any failure is swallowed so a fresh start is the worst case.
    /// </summary>
    public static void MigrateLegacyAppDataIfNeeded()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, "SilentStream");
            if (Directory.Exists(legacy) && !Directory.Exists(AppDataDir))
            {
                Directory.Move(legacy, AppDataDir);
            }
        }
        catch
        {
            // best-effort: migration must never block startup.
        }
    }
}
