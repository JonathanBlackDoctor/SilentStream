namespace SilentStream.Core.Models;

/// <summary>
/// Point-in-time performance metrics published by the orchestrator/encoder for the
/// control UI (upload bitrate, FPS, CPU/GPU usage). See plan §3.8.
/// </summary>
/// <param name="UploadBitrateKbps">Current RTMP upload bitrate in kbps.</param>
/// <param name="Fps">Frames per second pushed to the encoder.</param>
/// <param name="CpuPercent">Process CPU usage (0-100).</param>
/// <param name="GpuPercent">GPU encoder usage (0-100), -1 when unavailable.</param>
/// <param name="TimestampUtc">When the snapshot was captured.</param>
public sealed record MetricsSnapshot(
    double UploadBitrateKbps,
    double Fps,
    double CpuPercent,
    double GpuPercent,
    DateTime TimestampUtc)
{
    public static MetricsSnapshot Empty { get; } =
        new(0, 0, 0, -1, DateTime.UnixEpoch);
}
