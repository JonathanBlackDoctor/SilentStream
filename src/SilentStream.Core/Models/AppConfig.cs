using System.Globalization;
using System.Text.Json.Serialization;

namespace SilentStream.Core.Models;

/// <summary>
/// Root application configuration persisted to %AppData%\MediaCaptureHelper\config.json.
/// Mirrors the schema in plan §6. OAuth tokens are DPAPI-encrypted (Phase 1).
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Schema version. v1 = base plan §6; v2 adds the period-VOD + remote sections
    /// (확장계획서 §6); v3 adds the multi-source audio mixer (sources + master filters) and
    /// capture monitor/region selection; v4 adds the first-run "seed every real microphone"
    /// step (see <see cref="AudioConfig.MicsSeeded"/>); v5 adds the per-device room label
    /// (<see cref="DeviceName"/>) for multi-PC single-channel deployments; v6 adds the phone
    /// push-notification section (<see cref="Notifications"/>); v7 adds the adaptive
    /// stream-quality section (<see cref="EncodingConfig.Adaptive"/>); v8 adds approval-based
    /// period splitting (<see cref="PeriodsConfig.RequireApproval"/>) and seeds the built-in
    /// Mon–Fri timetable when no schedule exists; v9 adds first-install room provisioning
    /// (<see cref="ProvisioningConfig"/>); v11 standardizes live and per-period YouTube titles. Missing keys
    /// deserialize to their defaults, so an older file loads cleanly and is migrated on the next save.
    /// </summary>
    public int Version { get; set; } = 11;

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

    /// <summary>Phone push-notification settings (원격 컨트롤러 개선 Phase 1). Added in schema v6.</summary>
    public NotificationsConfig Notifications { get; set; } = new();

    /// <summary>
    /// First-install room provisioning state. The service URL lives in the non-secret bootstrap
    /// file shipped next to the installer; this section stores only the completed assignment and
    /// a random installation id. The Cloudflare token itself stays in <see cref="RemoteConfig"/>
    /// as a DPAPI blob.
    /// </summary>
    public ProvisioningConfig Provisioning { get; set; } = new();

    /// <summary>Global hotkey that toggles the control UI (plan §3.8).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>Auto-start mechanism: "startup" (registry Run) or "scheduler" (Task Scheduler).</summary>
    public string Autostart { get; set; } = "startup";

    /// <summary>
    /// Per-device room label (호실명, e.g. "201호") stamped onto live + VOD titles through the
    /// {호실} template token, so several PCs streaming to one shared channel stay distinguishable
    /// (e.g. "[201호] 1교시 - 2026-06-23"). Empty = no label: the "[{호실}] " prefix collapses to
    /// nothing rather than rendering "[] 라이브". Schema v5. Seeded from the machine name on a
    /// brand-new install only (ConfigStore.Load's no-file branch); an existing config is never
    /// silently stamped. Single source of truth for both title paths — do NOT duplicate per section.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the 6px broadcast-status box (plan §3.2) is shown at the top-left of the primary
    /// monitor. Defaults to false (hidden) — the control window's state badge already conveys the
    /// stream state. Toggle it from the control window's settings. A file written before this key
    /// existed deserializes to false, matching the new hidden-by-default behaviour.
    /// </summary>
    public bool ShowStatusBox { get; set; }

    /// <summary>Returns a fresh config populated with the documented defaults.</summary>
    public static AppConfig CreateDefault() => new();
}

public sealed class YouTubeConfig
{
    /// <summary>DPAPI-encrypted refresh token (base64). Empty until first login.</summary>
    public string RefreshTokenEnc { get; set; } = string.Empty;

    /// <summary>Broadcast privacy. Plan fixes this to "unlisted".</summary>
    public string Privacy { get; set; } = "unlisted";

    /// <summary>
    /// Title template for live broadcasts. Date tokens plus the room token are applied at session
    /// start; the default is "[LIVE] {호실} | {yyyy-MM-dd}".
    /// </summary>
    public string TitleTemplate { get; set; } = "[LIVE] {호실} | {yyyy-MM-dd}";
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

    /// <summary>Adaptive stream-quality settings (확장계획서_적응형송출품질 §9). Schema v7.</summary>
    public AdaptiveConfig Adaptive { get; set; } = new();
}

/// <summary>
/// Adaptive stream-quality settings (확장계획서_적응형송출품질 §9, schema v7). These gate only
/// the AUTOMATIC controller decisions — manual quality control from the phone remote works
/// regardless. The trigger/dwell windows are code constants (AdaptiveQualityController), not config.
/// </summary>
public sealed class AdaptiveConfig
{
    /// <summary>Master switch for automatic degradation on overload/congestion (D-AQ1: 기본 켬).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the controller may step back UP after conditions recover (D-AQ2: 기본 켬). The
    /// step-up defers to 쉬는 시간 (no class period running) so a working broadcast is not
    /// glitched mid-class. false = automatic changes only ever go down; recovery is manual.
    /// </summary>
    public bool AutoRecover { get; set; } = true;

    /// <summary>Deepest ladder level automatic degradation may reach (0-3; 0 = never degrade).</summary>
    public int MaxLevel { get; set; } = 3;
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

    /// <summary>
    /// True once the first-run seeding (schema v4) has populated <see cref="Sources"/> with the
    /// system loopback plus every real microphone present at install time. Guards that one-time
    /// step so microphones the user later removes are not re-added on the next launch. A
    /// pre-existing config (v1-v3) is treated as already seeded — only a brand-new install expands
    /// to all mics. Default false; set true by ConfigStore migration or the first-run seeder.
    /// </summary>
    public bool MicsSeeded { get; set; }

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

    /// <summary>Target folder. Default %USERPROFILE%\Videos\MediaCaptureHelper (resolved at runtime).</summary>
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

    /// <summary>
    /// Title template for uploaded period VODs. Period and date tokens plus the room token are
    /// applied at upload time; the default is "[영상] {호실} | {yyyy-MM-dd} | {교시}교시".
    /// </summary>
    public string TitleTemplate { get; set; } = "[영상] {호실} | {yyyy-MM-dd} | {교시}교시";

    /// <summary>VOD privacy; plan fixes this to "unlisted" (D4).</summary>
    public string VodPrivacy { get; set; } = "unlisted";

    /// <summary>"immediate-throttled" (default, D10) or "after-hours".</summary>
    public string UploadTiming { get; set; } = "immediate-throttled";

    /// <summary>Default auto-approve delay for pending splits, minutes. Single source of truth.</summary>
    public const int DefaultAutoApproveMinutes = 15;

    /// <summary>
    /// true (default, v8): a period end creates a pending split the operator approves / adjusts /
    /// merges from the phone before anything is cut or uploaded. false = legacy behaviour, cut +
    /// upload immediately at the boundary.
    /// </summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>
    /// Minutes after a period boundary before an untouched pending split is auto-approved at its
    /// default times (so an unattended room still converges on the legacy result). null = wait
    /// forever, manual approval only. Added in schema v8.
    /// </summary>
    public int? AutoApproveMinutes { get; set; } = DefaultAutoApproveMinutes;

    /// <summary>
    /// The built-in school timetable (v8): eight 60-minute periods from 08:25, lunch 12:25~13:25
    /// left as a plain gap (not a period → never cut/uploaded).
    /// </summary>
    public static List<PeriodEntry> DefaultWeekdayEntries()
    {
        var entries = new List<PeriodEntry>();
        var start = new TimeOnly(8, 25);
        for (var no = 1; no <= 8; no++)
        {
            if (no == 5)
            {
                start = new TimeOnly(13, 25); // resume after the lunch gap
            }
            var end = start.AddHours(1);
            entries.Add(new PeriodEntry
            {
                No = no,
                Start = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                End = end.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            });
            start = end;
        }
        return entries;
    }

    /// <summary>Fills Mon–Fri with the built-in timetable (weekends stay empty).</summary>
    public void SeedDefaultTimetable()
    {
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri" })
        {
            WeekdayDefaults[day] = DefaultWeekdayEntries();
        }
    }

    /// <summary>True if any weekday default holds at least one period row (guards the v8 seed).</summary>
    public bool HasAnyWeekdayPeriods() =>
        WeekdayDefaults is not null && WeekdayDefaults.Values.Any(list => list is { Count: > 0 });
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
    /// "remote.example.com"). Display only — shown in the control window so you know the phone
    /// URL. Optional; leave empty if you only run a quick tunnel.
    /// </summary>
    public string CloudflareHostname { get; set; } = string.Empty;

    /// <summary>
    /// cloudflared edge transport: "http2" (default), "quic", or "auto". QUIC needs outbound
    /// UDP/7844, which many managed/school networks block — the 2nd field test saw cloudflared fail
    /// with "failed to dial to edge with quic" and no automatic fallback, so the phone could not
    /// reach the tunnel until it was forced onto http2. http2 uses TCP/443 and works wherever HTTPS
    /// does, so it is the default for unattended reliability. Blank is treated as "http2" at load.
    /// </summary>
    public string CloudflareProtocol { get; set; } = "http2";
}

/// <summary>
/// Durable state for the room-selection provisioning flow (schema v9). No server credential or
/// tunnel token is stored here: the server endpoint is public configuration and the received
/// token is immediately DPAPI-protected in <see cref="RemoteConfig.CloudflareTunnelTokenEnc"/>.
/// </summary>
public sealed class ProvisioningConfig
{
    /// <summary>Stable random id used by the provisioning service to recognise a reinstall.</summary>
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>Identifier of the assigned room (for example, "m111").</summary>
    public string RoomId { get; set; } = string.Empty;

    /// <summary>True once an assignment was successfully written to the local encrypted config.</summary>
    public bool Completed { get; set; }

    /// <summary>UTC timestamp of the successful assignment, useful for support diagnostics.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>
/// Phone push-notification settings (원격 컨트롤러 개선 Phase 1, schema v6). The health layer's
/// events (mic silent / rtmp down / disk low / upload failed …) are pushed to the operator's phone
/// through the configured channels — Telegram in Phase 1; PWA Web Push joins in Phase 3.
/// </summary>
public sealed class NotificationsConfig
{
    /// <summary>
    /// Master switch. Defaults to true so pasting a bot token + chat id "just works" — with no
    /// credentials configured the notifier is a no-op anyway, so the default is harmless.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Telegram bot token (from @BotFather). Sensitive — paste the plaintext token here ONCE (or
    /// enter it in the control window, which encrypts immediately); on first use the app encrypts
    /// it into <see cref="TelegramBotTokenEnc"/> (DPAPI) and wipes this field, so it is never left
    /// at rest in plaintext. Mirrors <see cref="RemoteConfig.CloudflareTunnelToken"/>.
    /// </summary>
    public string TelegramBotToken { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-encrypted Telegram bot token (base64, CurrentUser scope) — the at-rest form actually
    /// used. Empty until a token is configured. Mirrors <see cref="RemoteConfig.CloudflareTunnelTokenEnc"/>.
    /// </summary>
    public string TelegramBotTokenEnc { get; set; } = string.Empty;

    /// <summary>Chat id the bot sends to (not secret — stored plain, like <see cref="RemoteConfig.CloudflareHostname"/>).</summary>
    public string TelegramChatId { get; set; } = string.Empty;

    /// <summary>
    /// Minimum severity that triggers a push: "info" | "warn" | "critical". Default "warn" so the
    /// operational alerts (무음/송출 끊김/디스크 부족/업로드 실패) push while routine live start/stop
    /// stays in the log. Blank is treated as "warn" at load.
    /// </summary>
    public string NotifyLevel { get; set; } = "warn";
}
