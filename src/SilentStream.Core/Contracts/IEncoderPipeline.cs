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

    /// <summary>Raised on each metrics poll from the encoder (bitrate/fps).</summary>
    event EventHandler<MetricsSnapshot> MetricsUpdated;

    /// <summary>Starts the tee pipeline (RTMP + mp4) with the given options.</summary>
    Task StartAsync(EncoderStartOptions options, CancellationToken ct);

    /// <summary>Gracefully stops the encoder (sends 'q' to finalise the mp4).</summary>
    Task StopAsync();
}
