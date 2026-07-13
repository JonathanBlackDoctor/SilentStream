using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Runtime central coordinator / state machine. Owns component lifecycles, retries,
/// and publishes state and metrics for the status box and control UI. See plan §4 / §2.2.
/// </summary>
public interface IStreamOrchestrator
{
    /// <summary>Current pipeline state.</summary>
    StreamState State { get; }

    /// <summary>Raised whenever <see cref="State"/> transitions.</summary>
    event EventHandler<StreamState> StateChanged;

    /// <summary>Raised on each metrics poll (bps, fps, cpu, gpu).</summary>
    event EventHandler<MetricsSnapshot> MetricsUpdated;

    /// <summary>
    /// The encode-quality level currently applied to the pipeline (적응형 송출 품질). Starts at
    /// <see cref="QualityStatus.Original"/> and changes only through a successful in-place encoder
    /// swap or a session reset — never speculatively.
    /// </summary>
    QualityStatus CurrentQuality { get; }

    /// <summary>Raised when the applied quality level or mode changes.</summary>
    event EventHandler<QualityStatus> QualityChanged;

    /// <summary>Runs the start sequence (warmup → connect → live).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Runs the stop sequence (flush encoder, finalise mp4, complete broadcast).</summary>
    Task StopAsync();

    /// <summary>
    /// Stops only the YouTube broadcast while the local backup recording continues
    /// (→ <see cref="StreamState.RecordingOnly"/>). Because the tee encoder is a single process,
    /// the current mp4 is finalised and recording resumes in a new part file. No-op unless
    /// <see cref="State"/> is <see cref="StreamState.Live"/>; degenerates to <see cref="StopAsync"/>
    /// when recording is disabled (nothing to keep).
    /// </summary>
    Task StopStreamingKeepRecordingAsync();

    /// <summary>
    /// Expedites a pending reconnect, or rebuilds the active live encoder once to recover its
    /// ingest path. Returns false when the pipeline is not in a state that can be recovered
    /// without changing the operator's intent (for example, Idle or recording-only).
    /// </summary>
    Task<bool> ForceRetryAsync();
}
