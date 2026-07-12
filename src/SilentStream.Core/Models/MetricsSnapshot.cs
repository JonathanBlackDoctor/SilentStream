namespace SilentStream.Core.Models;

/// <summary>
/// Point-in-time performance metrics published by the orchestrator/encoder for the control UI,
/// phone remote and the adaptive-quality controller (plan §3.8, 확장계획서_적응형송출품질 §5.1).
/// The rate fields are WINDOWED recent values: ffmpeg's own fps=/bitrate= stats are
/// session-cumulative averages that dilute any sudden sag on a long session, so the pipeline
/// recomputes recent rates from the cumulative counters below (see MetricsWindower).
/// </summary>
/// <param name="UploadBitrateKbps">Recent-window output bitrate in kbps. Stays 0 on live tee
/// sessions — ffmpeg reports size=N/A under the tee muxer (실측), so only recording-only
/// sessions carry a real value.</param>
/// <param name="Fps">Recent-window frames per second actually encoded.</param>
/// <param name="CpuPercent">Host (WPF) process CPU usage (0-100) — stamped by the orchestrator;
/// the encode cost lives in <paramref name="EncoderCpuPercent"/> instead.</param>
/// <param name="GpuPercent">GPU encoder usage (0-100), -1 when unavailable.</param>
/// <param name="TimestampUtc">When the snapshot was captured.</param>
/// <param name="FrameCount">Cumulative frames encoded this session (ffmpeg frame= counter).</param>
/// <param name="OutputKBytes">Cumulative muxed output in 1024-byte units (ffmpeg size=, KiB);
/// 0 when ffmpeg reports N/A (live tee).</param>
/// <param name="TeeDropCount">Cumulative RTMP packets dropped by the tee fifo this session
/// (drop_pkts_on_overflow) — the uplink-congestion signal (§5.2 NET trigger).</param>
/// <param name="EncoderCpuPercent">ffmpeg child-process CPU (0-100), -1 when unavailable.</param>
public sealed record MetricsSnapshot(
    double UploadBitrateKbps,
    double Fps,
    double CpuPercent,
    double GpuPercent,
    DateTime TimestampUtc,
    long FrameCount = 0,
    long OutputKBytes = 0,
    int TeeDropCount = 0,
    double EncoderCpuPercent = -1)
{
    public static MetricsSnapshot Empty { get; } =
        new(0, 0, 0, -1, DateTime.UnixEpoch);
}
