using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// The adaptive-quality POLICY layer (확장계획서_적응형송출품질 §3): watches windowed encoder
/// metrics, decides when to step the quality ladder down (encode overload / uplink congestion) or
/// back up (recovery — deferred while a class period is running), and raises
/// <see cref="ChangeRequested"/>. It owns the DESIRED level; the orchestrator executes swaps and
/// publishes the APPLIED truth (IStreamOrchestrator.CurrentQuality). Manual pinning from the
/// phone remote suspends automatic changes in both directions (D-AQ4: session-scoped).
/// </summary>
public interface IAdaptiveQualityController
{
    /// <summary>Auto (controller-driven) or ManualHold (operator-pinned).</summary>
    QualityMode Mode { get; }

    /// <summary>Desired ladder level (0 = 원본). Applied truth lives on the orchestrator.</summary>
    int Level { get; }

    /// <summary>The per-session quality ladder; empty until the first <see cref="Apply"/> builds it.</summary>
    IReadOnlyList<QualityStep> Ladder { get; }

    /// <summary>Snapshot of the desired quality state (mode/level/step/reason).</summary>
    QualityStatus Status { get; }

    /// <summary>
    /// Raised when the desired level or mode changes. The orchestrator swaps the live encoder in
    /// place when the level differs from the applied one, else just republishes the status.
    /// </summary>
    event EventHandler<QualityStatus>? ChangeRequested;

    /// <summary>
    /// Applies the current desired level to freshly built base encoder options — the single
    /// funnel: every encoder start (initial, reconnect, watchdog rebuild, in-place swap) passes
    /// through here, so all paths follow the same level. Also (re)builds the ladder from the base
    /// parameters and begins a fresh settle window.
    /// </summary>
    EncoderStartOptions Apply(EncoderStartOptions baseOptions);

    /// <summary>Feeds one windowed metrics tick (~2s cadence while the encoder runs).</summary>
    void OnMetrics(MetricsSnapshot metrics);

    /// <summary>Tracks the pipeline state; Idle resets to 원본/자동 (session-scoped holds).</summary>
    void OnStateChanged(StreamState state);

    /// <summary>Pins the level (phone remote / UI); automatic changes stop until cleared.</summary>
    void SetManual(int level);

    /// <summary>Clears a manual pin and resumes automatic control at the current level.</summary>
    void SetAuto();
}
