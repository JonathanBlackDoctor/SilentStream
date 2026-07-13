namespace SilentStream.Core.Models;

/// <summary>
/// How urgent a <see cref="HealthEvent"/> is. Consumers (phone push, control UI banner, logs)
/// filter on this: e.g. only <see cref="Critical"/> triggers an immediate lock-screen alert while
/// <see cref="Warn"/> can be batched. There is deliberately no 1:1 mapping to the 4 log levels —
/// <see cref="Critical"/> is logged via <see cref="Contracts.ILogService.Error"/>.
/// </summary>
public enum HealthSeverity
{
    /// <summary>Informational transition (e.g. live started/stopped). Not a problem.</summary>
    Info,

    /// <summary>A degraded condition worth surfacing but not yet urgent.</summary>
    Warn,

    /// <summary>An active outage/failure that needs prompt attention.</summary>
    Critical
}

/// <summary>
/// The kind of health condition an <see cref="HealthEvent"/> reports. This is the typed vocabulary
/// the health/event layer emits so downstream features (Phase 1 폰 푸시, Phase 2 멀티 호실, UI)
/// can react by kind rather than parsing log strings. See 확장계획서 (원격 컨트롤러 개선 로드맵 Phase 0).
/// </summary>
public enum HealthEventKind
{
    /// <summary>The live broadcast reached the Live state (informational, momentary).</summary>
    LiveStarted,

    /// <summary>The live broadcast stopped gracefully (informational, momentary).</summary>
    LiveStopped,

    /// <summary>
    /// The stream connection failed and the pipeline is stuck retrying (StreamState.Retrying) past
    /// the debounce window. Warn on first trip, escalates to Critical if it persists; cleared when
    /// the stream returns to Live or is stopped.
    /// </summary>
    RtmpDown,

    /// <summary>
    /// A capturing microphone fell silent (already grace-debounced by the mixer). Keyed by the mic
    /// source id; cleared when signal returns. Warn normally, Critical while a live session is active.
    /// </summary>
    MicSilent,

    /// <summary>
    /// Free space on the recording volume dropped below the configured minimum. Warn below the
    /// threshold, Critical below half of it; cleared (with a hysteresis margin) when space recovers.
    /// </summary>
    DiskLow,

    /// <summary>A VOD upload job exhausted its retries and is terminally failed (momentary, once per job).</summary>
    UploadFailed,

    /// <summary>
    /// A period-end VOD cut is waiting for operator approval (승인 기반 교시 분할; momentary, once
    /// per split). Emitted at Info so the default NotifyLevel("warn") keeps Telegram quiet — an
    /// operator who wants boundary pings opts in by lowering the level to info.
    /// </summary>
    SplitPending,

    /// <summary>
    /// The YouTube OAuth credential is expiring/invalid. RESERVED: no runtime source exposes token
    /// expiry today (see IYouTubeLiveService), so the health layer does not emit this yet — wiring it
    /// requires a small new hook on the auth service. Defined here so consumers can already handle it.
    /// </summary>
    OauthExpiring,

    /// <summary>
    /// The adaptive controller lowered the stream quality (확장계획서_적응형송출품질 §8). A
    /// condition: active while an automatic degradation is in effect (Warn; Critical at the 안전
    /// 모드 floor), cleared on recovery to 원본 or when an operator pins the level manually. A
    /// manual pin itself is a momentary Info. NOTE: the enum member name is serialized to the
    /// phone via ToString() — it is a wire contract.
    /// </summary>
    QualityDegraded
}

/// <summary>
/// A single typed health signal emitted by <see cref="Contracts.IHealthMonitor"/>. Condition kinds
/// (<see cref="HealthEventKind.RtmpDown"/>, <see cref="HealthEventKind.MicSilent"/>,
/// <see cref="HealthEventKind.DiskLow"/>) toggle <see cref="Active"/> on onset (true) and recovery
/// (false); momentary kinds (live started/stopped, upload failed) are always <see cref="Active"/>=true.
/// </summary>
/// <param name="Kind">Which condition this reports.</param>
/// <param name="Severity">Urgency of the event.</param>
/// <param name="Active">True on onset of a condition; false when it clears. Always true for momentary kinds.</param>
/// <param name="Message">User-facing Korean message for the UI/push/log.</param>
/// <param name="SourceKey">Sub-identity within the kind (mic source id, upload job id), or null.</param>
/// <param name="Room">The device's 호실명 (AppConfig.DeviceName) stamp, or null when unlabelled.</param>
/// <param name="TimestampUtc">When the event was raised (UTC).</param>
public sealed record HealthEvent(
    HealthEventKind Kind,
    HealthSeverity Severity,
    bool Active,
    string Message,
    string? SourceKey,
    string? Room,
    DateTime TimestampUtc);
