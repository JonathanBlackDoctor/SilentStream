namespace SilentStream.Core.Models;

/// <summary>Who controls the encode-quality level (적응형 송출 품질, 확장계획서_적응형송출품질 §7.1).</summary>
public enum QualityMode
{
    /// <summary>The adaptive controller may lower/restore the level automatically.</summary>
    Auto,

    /// <summary>
    /// An operator pinned the level (phone remote / UI); automatic changes are suspended in both
    /// directions until cleared or the session ends (D-AQ4: session-scoped).
    /// </summary>
    ManualHold
}

/// <summary>Why the applied quality level last changed. Drives health/log message wording.</summary>
public enum QualityChangeReason
{
    /// <summary>No change yet (initial state).</summary>
    None,

    /// <summary>Windowed encode fps stayed persistently below target — the device can't keep up.</summary>
    EncodeOverload,

    /// <summary>The RTMP tee fifo kept dropping packets — the uplink can't carry the bitrate.</summary>
    NetworkCongestion,

    /// <summary>Conditions stayed healthy long enough to step back up.</summary>
    AutoRecover,

    /// <summary>An operator set the level manually (phone remote / UI).</summary>
    ManualSet,

    /// <summary>The session ended and the level reset to the original.</summary>
    SessionReset
}

/// <summary>
/// One rung of the per-session quality ladder (§4). Values are concrete: the ladder is derived
/// from the session's resolved base encode parameters when it is built, so consumers never need
/// to re-resolve "source"/auto placeholders. Audio is deliberately absent — it is never degraded.
/// </summary>
/// <param name="Level">0 = original (base), higher = more degraded.</param>
/// <param name="Name">User-facing Korean label ("원본", "절약 1단계", …).</param>
/// <param name="Width">Encode width in pixels.</param>
/// <param name="Height">Encode height in pixels.</param>
/// <param name="Fps">Encode frame rate.</param>
/// <param name="VideoBitrateKbps">Target video bitrate (kbps).</param>
public sealed record QualityStep(
    int Level,
    string Name,
    int Width,
    int Height,
    double Fps,
    int VideoBitrateKbps);

/// <summary>
/// The quality level currently applied to the encoder pipeline — the orchestrator's single source
/// of truth, published via IStreamOrchestrator.CurrentQuality/QualityChanged and consumed by the
/// control window, the phone remote and the health layer.
/// </summary>
/// <param name="Mode">Auto (controller-driven) or ManualHold (operator-pinned).</param>
/// <param name="Level">Applied ladder level; 0 = original.</param>
/// <param name="LevelName">User-facing Korean label of the applied level.</param>
/// <param name="Reason">Why the level last changed.</param>
/// <param name="Applied">Concrete encode parameters of the applied level; null until known.</param>
/// <param name="DegradedSinceUtc">When the level first left 0, or null while at the original.</param>
public sealed record QualityStatus(
    QualityMode Mode,
    int Level,
    string LevelName,
    QualityChangeReason Reason,
    QualityStep? Applied,
    DateTime? DegradedSinceUtc)
{
    /// <summary>Level 0 / auto — the state before any adaptation (원본 품질).</summary>
    public static QualityStatus Original { get; } =
        new(QualityMode.Auto, 0, "원본", QualityChangeReason.None, null, null);
}
