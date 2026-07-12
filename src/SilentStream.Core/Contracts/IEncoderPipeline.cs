using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Encodes the captured A/V once and tees it to RTMP (onfail=ignore) and a local mp4
/// simultaneously via FFmpeg, with GPU auto-detection (NVENC→AMF→QSV→x264). See plan §3.5/§3.6.
/// </summary>
public interface IEncoderPipeline : IDisposable
{
    /// <summary>True while the FFmpeg process is running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Time since the last ffmpeg progress line while the encoder is intended to be running
    /// (<see cref="TimeSpan.Zero"/> when stopped). Lets the watchdog catch a stalled feed where
    /// the process is alive but no longer emitting frames (e.g. capture/pipe EOF).
    /// </summary>
    TimeSpan TimeSinceProgress { get; }

    /// <summary>
    /// True once the RTMP tee slave has failed for the current session (open/write error absorbed
    /// by onfail=ignore) while the mp4 leg keeps the process alive and progress flowing — a dead
    /// broadcast that is otherwise invisible to the liveness signals above. The watchdog rebuilds
    /// the pipeline when it sees this (확장계획서_적응형송출품질 §5.4). Reset by
    /// <see cref="StartAsync"/>.
    /// </summary>
    bool RtmpLegDown { get; }

    /// <summary>Raised on each metrics poll from the encoder (bitrate/fps).</summary>
    event EventHandler<MetricsSnapshot> MetricsUpdated;

    /// <summary>Raised when ffmpeg exits while it was intended to be running (crash/kill).</summary>
    event EventHandler? UnexpectedExit;

    /// <summary>Starts the tee pipeline (RTMP + mp4) with the given options.</summary>
    Task StartAsync(EncoderStartOptions options, CancellationToken ct);

    /// <summary>Gracefully stops the encoder (sends 'q' to finalise the mp4).</summary>
    Task StopAsync();
}
