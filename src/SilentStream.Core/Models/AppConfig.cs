using System.Text.Json.Serialization;

namespace SilentStream.Core.Models;

/// <summary>
/// Root application configuration persisted to %AppData%\SilentStream\config.json.
/// Mirrors the schema in plan §6. OAuth tokens are DPAPI-encrypted (Phase 1).
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Schema version. v1 = base plan §6; v2 adds the period-VOD + remote sections
    /// (확장계획서 §6); v3 adds the multi-source audio mixer (sources + master filters) and
    /// capture monitor/region selection. Missing keys deserialize to their defaults, so an
    /// older file loads cleanly and is migrated on the next save.
    /// </summary>
    public int Version { get; set; } = 3;

    // camelCase would yield "youTube"; the documented schema (plan §6) uses "youtube".
    [JsonPropertyName("youtube")]
    public YouTubeConfig YouTube { get; set; } = new();

    public EncodingConfig Encoding { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    /// <summary>Capture monitor + optional crop region (schema v3).</summary>
    public CaptureConfig Capture { get; set; } = new();

    public RecordingConfig Recording { get; set; } = new();

    /// <summary>Class-timetable + VOD settings (확장계획서 §6). Added in schema v2.</summary>
    public PeriodsConfig Periods { get; set; } = new();

    /// <summary>Smartphone remote-control server settings (확장계획서 §6). Added in schema v2.</summary>
    public RemoteConfig Remote { get; set; } = new();

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

    /// <summary>"source" follows the captured monitor, else "WxH" (e.g. "1280x720") to scale.</summary>
    public string Resolution { get; set; } = "source";

    /// <summary>"source" follows the captured monitor refresh rate, else a number (e.g. "24").</summary>
    public string Fps { get; set; } = "source";

    /// <summary>Manual video bitrate in kbps; 0 = auto (YouTube-recommended for the output size).</summary>
    public int VideoBitrateKbps { get; set; }

    /// <summary>Audio (AAC) bitrate in kbps. Default 160.</summary>
    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>"25" | "50" | "75" | "none" — approximate resource cap (plan §3.5).</summary>
    public string ResourceLimit { get; set; } = "none";
}

public sealed class AudioConfig
{
    // ---- Legacy v1/v2 fields (kept for backward compatibility + migration into Sources) ----

    /// <summary>System (loopback) volume, 0.0-1.0. Legacy; migrated into <see cref="Sources"/>.</summary>
    public double SystemVolume { get; set; } = 1.0;

    /// <summary>Microphone volume, 0.0-1.0. Legacy; migrated into <see cref="Sources"/>.</summary>
    public double MicVolume { get; set; } = 1.0;

    /// <summary>Selected microphone device id, null = system default. Legacy; migrated into <see cref="Sources"/>.</summary>
    public string? MicDeviceId { get; set; }

    // ---- v3: explicit multi-source mixer ----

    /// <summary>
    /// The mixer sources: one system loopback plus any number of microphones. Empty on a v2 file;
    /// ConfigStore migrates the legacy fields into this list on load.
    /// </summary>
    public List<AudioSourceConfig> Sources { get; set; } = new();

    /// <summary>Master-bus post-processing (denoise/compressor/limiter/gain), applied next session.</summary>
    public AudioFiltersConfig Filters { get; set; } = new();
}

/// <summary>One mixer source as persisted in config.json (schema v3).</summary>
public sealed class AudioSourceConfig
{
    /// <summary>"system" or "mic".</summary>
    public string Kind { get; set; } = "mic";

    /// <summary>Capture device id; null = system default (loopback / communications mic).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Display label shown in the mixer UI (falls back to the device name when empty).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Linear gain. 0 = silence, 1 = unity, &gt;1 amplifies (UI caps ~4 ≈ +12 dB).</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Whether the source is muted.</summary>
    public bool Muted { get; set; }

    /// <summary>Whether the per-source noise gate is enabled.</summary>
    public bool GateEnabled { get; set; }

    /// <summary>Noise-gate open threshold in dBFS (e.g. -45).</summary>
    public double GateThresholdDb { get; set; } = -45;
}

/// <summary>Master-bus audio filter settings (schema v3). Applied by FFmpeg at session start.</summary>
public sealed class AudioFiltersConfig
{
    public bool NoiseSuppressionEnabled { get; set; }

    /// <summary>afftdn reduction in dB (1-97).</summary>
    public int NoiseSuppressionDb { get; set; } = 12;

    public bool CompressorEnabled { get; set; }

    public bool LimiterEnabled { get; set; }

    /// <summary>Master make-up gain in dB applied after the filters.</summary>
    public double MasterGainDb { get; set; }
}

/// <summary>Capture monitor + optional crop region (schema v3, OBS 대비 모니터/영역 선택).</summary>
public sealed class CaptureConfig
{
    /// <summary>0-based monitor ordinal across all DXGI outputs. 0 = primary (default).</summary>
    public int MonitorIndex { get; set; }

    /// <summary>Crop region within the monitor. Zero width/height = capture the whole monitor.</summary>
    public int RegionX { get; set; }

    public int RegionY { get; set; }

    public int RegionWidth { get; set; }

    public int RegionHeight { get; set; }
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

/// <summary>
/// Class-timetable + per-period VOD settings (확장계획서 §6). Weekday defaults are keyed by
/// the 3-letter English weekday abbreviation ("Mon".."Sun"); per-date overrides are keyed by
/// ISO date ("yyyy-MM-dd"). Empty maps simply mean "no periods → nothing is cut/uploaded".
/// </summary>
public sealed class PeriodsConfig
{
    /// <summary>Per-weekday default schedules, keyed "Mon".."Sun".</summary>
    public Dictionary<string, List<PeriodEntry>> WeekdayDefaults { get; set; } = new();

    /// <summary>Per-date overrides, keyed "yyyy-MM-dd" (D5: 그날 통째 덮어쓰기).</summary>
    public Dictionary<string, List<PeriodEntry>> Overrides { get; set; } = new();

    /// <summary>Title template for uploaded period VODs. {교시}/{교시:00} + date tokens (D6).</summary>
    public string TitleTemplate { get; set; } = "{교시}교시 - {yyyy-MM-dd}";

    /// <summary>VOD privacy; plan fixes this to "unlisted" (D4).</summary>
    public string VodPrivacy { get; set; } = "unlisted";

    /// <summary>"immediate-throttled" (default, D10) or "after-hours".</summary>
    public string UploadTiming { get; set; } = "immediate-throttled";
}

/// <summary>One timetable row as stored in config.json: {"no":1,"start":"09:00:00","end":"09:50:00"}.</summary>
public sealed class PeriodEntry
{
    /// <summary>1-based period number (1교시, 2교시 …). Serializes as "no".</summary>
    [JsonPropertyName("no")]
    public int No { get; set; }

    /// <summary>Local start time, "HH:mm:ss".</summary>
    public string Start { get; set; } = "00:00:00";

    /// <summary>Local end time, "HH:mm:ss".</summary>
    public string End { get; set; } = "00:00:00";
}

/// <summary>Smartphone remote-control server settings (확장계획서 §6, D8/D11).</summary>
public sealed class RemoteConfig
{
    /// <summary>"off" (default), "lan", "tailscale", or "cloudflare" — see RemoteBindMode.</summary>
    public string Mode { get; set; } = "off";

    /// <summary>TCP port the embedded Kestrel server binds to.</summary>
    public int Port { get; set; } = 8787;

    /// <summary>SHA-256 hashes of paired device tokens (D11). Never stores raw tokens.</summary>
    public List<string> DeviceTokens { get; set; } = new();

    /// <summary>
    /// Cloudflare named-tunnel token (used when Mode="cloudflare"). The tunnel dials outbound to the
    /// Cloudflare edge and serves the loopback server over HTTPS, so no port-forwarding is needed.
    /// Sensitive — paste the plaintext token here ONCE; on the next start the app encrypts it into
    /// <see cref="CloudflareTunnelTokenEnc"/> (DPAPI) and wipes this field, so it is never left at rest
    /// in plaintext. Empty = run an ephemeral quick tunnel instead (random *.trycloudflare.com URL).
    /// </summary>
    public string CloudflareTunnelToken { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted Cloudflare named-tunnel token (base64, CurrentUser scope). Written by the app
    /// after migrating a pasted <see cref="CloudflareTunnelToken"/>; this is the at-rest form actually
    /// used. Empty until a token is configured. Mirrors <see cref="YouTubeConfig.RefreshTokenEnc"/>.
    /// </summary>
    public string CloudflareTunnelTokenEnc { get; set; } = string.Empty;

    /// <summary>
    /// Public hostname mapped to this tunnel in the Cloudflare dashboard (e.g.
    /// "silentstream.example.com"). Display only — shown in the control window so you know the phone
    /// URL. Optional; leave empty if you only run a quick tunnel.
    /// </summary>
    public string CloudflareHostname { get; set; } = string.Empty;
}
