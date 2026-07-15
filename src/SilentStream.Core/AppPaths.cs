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

    /// <summary>
    /// DPAPI-encrypted OAuth credential used only for authorized YouTube caption downloads. It is
    /// separate from the live-stream token so granting <c>youtube.force-ssl</c> does not disrupt
    /// an existing broadcast login.
    /// </summary>
    public static string YouTubeCaptionTokenFile => Path.Combine(AppDataDir, "youtube_caption_token.dat");

    /// <summary>Scratch folder for per-period VOD cuts awaiting upload (확장계획서 §4.1).</summary>
    public static string VodDir => Path.Combine(AppDataDir, "vod");

    /// <summary>Persistent, quota-aware upload queue (확장계획서 §4.2). Not sensitive.</summary>
    public static string UploadQueueFile => Path.Combine(AppDataDir, "upload_queue.json");

    /// <summary>Approval-based split state (승인 기반 교시 분할): pending cuts + open 연강 chain.</summary>
    public static string PendingSplitsFile => Path.Combine(AppDataDir, "pending_splits.json");

    /// <summary>Web Push subscriptions (원격 컨트롤러 개선 Phase 3). Not sensitive — endpoint + public keys.</summary>
    public static string PushSubscriptionsFile => Path.Combine(AppDataDir, "push_subscriptions.json");

    /// <summary>VAPID keypair (원격 컨트롤러 개선 Phase 3). The private scalar is DPAPI-encrypted at rest.</summary>
    public static string VapidKeysFile => Path.Combine(AppDataDir, "vapid_keys.json");

    /// <summary>
    /// Durable download assets for completed period VODs. This is intentionally separate from
    /// <see cref="VodDir"/>: the upload worker removes its temporary MP4 after YouTube accepts it,
    /// while exported audio remains available to the paired operator.
    /// </summary>
    public static string PeriodAssetsDir => Path.Combine(AppDataDir, "period-assets");

    /// <summary>Metadata catalogue for <see cref="PeriodAssetsDir"/>.</summary>
    public static string PeriodAssetsFile => Path.Combine(PeriodAssetsDir, "assets.json");

    /// <summary>
    /// Locally-exported audio from this app's own period recordings. It is never downloaded from
    /// YouTube, which keeps the feature within YouTube API policy.
    /// </summary>
    public static string PeriodAudioDir => Path.Combine(PeriodAssetsDir, "audio");

    /// <summary>
    /// Short-lived source media downloaded while rebuilding a missing period-audio asset.
    /// Completed M4A files never live here and stale subdirectories are removed on startup.
    /// </summary>
    public static string PeriodAudioRecoveryTempDir => Path.Combine(PeriodAssetsDir, "recovery-temp");

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
