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
}
