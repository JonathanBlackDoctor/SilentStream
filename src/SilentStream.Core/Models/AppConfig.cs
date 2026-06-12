namespace SilentStream.Core.Models;

/// <summary>
/// Root application configuration persisted to %AppData%\SilentStream\config.json.
/// Mirrors the schema in plan §6. OAuth tokens are DPAPI-encrypted (Phase 1).
/// </summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public YouTubeConfig YouTube { get; set; } = new();

    public EncodingConfig Encoding { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    public RecordingConfig Recording { get; set; } = new();

    /// <summary>Global hotkey that toggles the control UI (plan §3.8).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Auto-start mechanism: "startup" (registry Run) or "scheduler" (Task Scheduler).</summary>
    public string Autostart { get; set; } = "startup";

    /// <summary>Returns a fresh config populated with the documented defaults.</summary>
    public static AppConfig CreateDefault() => new();
}

public sealed class YouTubeConfig
{
    /// <summary>DPAPI-encrypted refresh token (base64). Empty until first login.</summary>
    public string RefreshTokenEnc { get; set; } = string.Empty;

    /// <summary>Broadcast privacy. Plan fixes this to "unlisted".</summary>
    public string Privacy { get; set; } = "unlisted";

    /// <summary>Title template; {0:...}/format tokens applied at session start.</summary>
    public string TitleTemplate { get; set; } = "라이브 - {yyyy-MM-dd HH:mm}";
}

public sealed class EncodingConfig
{
    /// <summary>"auto" | "nvenc" | "amf" | "qsv" | "x264".</summary>
    public string PreferredGpu { get; set; } = "auto";

    /// <summary>"source" follows the primary monitor.</summary>
    public string Resolution { get; set; } = "source";

    /// <summary>"source" follows the primary monitor refresh rate.</summary>
    public string Fps { get; set; } = "source";

    /// <summary>"25" | "50" | "75" | "none" — approximate resource cap (plan §3.5).</summary>
    public string ResourceLimit { get; set; } = "none";
}

public sealed class AudioConfig
{
    /// <summary>System (loopback) volume, 0.0-1.0.</summary>
    public double SystemVolume { get; set; } = 1.0;

    /// <summary>Microphone volume, 0.0-1.0.</summary>
    public double MicVolume { get; set; } = 1.0;

    /// <summary>Selected microphone device id, null = system default.</summary>
    public string? MicDeviceId { get; set; }
}

public sealed class RecordingConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Target folder. Default %USERPROFILE%\Videos\SilentStream (resolved at runtime).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Capacity cap in GB; oldest files deleted when exceeded (plan §3.6).</summary>
    public int MaxSizeGb { get; set; } = 100;

    /// <summary>Retention window in days; older files auto-deleted.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Minimum free space in GB before recording is paused (plan §3.6).</summary>
    public int MinFreeGb { get; set; } = 5;
}
