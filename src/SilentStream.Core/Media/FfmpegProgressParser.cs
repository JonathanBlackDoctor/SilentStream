using System.Globalization;
using System.Text.RegularExpressions;
using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>
/// Interprets FFmpeg's stderr: periodic stats lines
/// (e.g. "frame= 1234 fps= 30 ... size=  373KiB ... bitrate=6021.3kbits/s ...") into metrics,
/// plus the tee/fifo warning lines the adaptive-quality layer feeds on (확장계획서_적응형송출품질
/// §5). ffmpeg's fps=/bitrate= fields are session-cumulative averages, so the cumulative
/// frame=/size= counters are captured too and <see cref="MetricsWindower"/> recovers true recent
/// rates from tick deltas. Message templates are pinned against the bundled ffmpeg 8.1
/// (실측 fixtures in MediaTests).
/// </summary>
public static partial class FfmpegProgressParser
{
    [GeneratedRegex(@"fps=\s*(?<fps>[\d.]+)")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"bitrate=\s*(?<rate>[\d.]+)\s*kbits/s")]
    private static partial Regex BitrateRegex();

    [GeneratedRegex(@"frame=\s*(?<frame>\d+)")]
    private static partial Regex FrameRegex();

    // 8.x prints "KiB", older builds "kB" (both 1024-byte units). The final report's "Lsize="
    // still contains "size=" and matches too, which is fine (it is the same total). "size=N/A"
    // (the live tee muxer cannot report a size) simply doesn't match → 0.
    [GeneratedRegex(@"size=\s*(?<size>\d+)\s*Ki?B", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>Returns a snapshot if the line carries stats, otherwise null.</summary>
    public static MetricsSnapshot? TryParse(string line, DateTime timestampUtc)
    {
        var fpsMatch = FpsRegex().Match(line);
        var rateMatch = BitrateRegex().Match(line);
        var frameMatch = FrameRegex().Match(line);
        if (!fpsMatch.Success && !rateMatch.Success && !frameMatch.Success)
        {
            return null;
        }

        var fps = fpsMatch.Success
            ? double.Parse(fpsMatch.Groups["fps"].Value, CultureInfo.InvariantCulture)
            : 0;
        var rate = rateMatch.Success
            ? double.Parse(rateMatch.Groups["rate"].Value, CultureInfo.InvariantCulture)
            : 0;
        var frame = frameMatch.Success
            ? long.Parse(frameMatch.Groups["frame"].Value, CultureInfo.InvariantCulture)
            : 0;
        var sizeMatch = SizeRegex().Match(line);
        var size = sizeMatch.Success
            ? long.Parse(sizeMatch.Groups["size"].Value, CultureInfo.InvariantCulture)
            : 0;

        return new MetricsSnapshot(rate, fps, CpuPercent: 0, GpuPercent: -1, timestampUtc,
            FrameCount: frame, OutputKBytes: size);
    }

    /// <summary>
    /// True for the tee-fifo's queue-full warning — emitted when the RTMP slave drains slower than
    /// realtime and drop_pkts_on_overflow discards packets. The encoder's own stats stay nominal
    /// while this happens, so these lines are the ONLY observable sign of uplink congestion
    /// (§5.2 NET trigger). ffmpeg 8.1 실측: "[fifo @ 0x…] FIFO queue full".
    /// </summary>
    public static bool IsTeeDropLine(string line) =>
        line.Contains("FIFO queue full", StringComparison.Ordinal);

    /// <summary>
    /// True when a tee slave carrying the RTMP leg failed (open/write/trailer). Behind
    /// onfail=ignore the process keeps encoding to the mp4 slave and progress keeps flowing, so a
    /// dead broadcast is otherwise invisible to the watchdog (the -10053 field bug, §5.4).
    /// ffmpeg 8.1 templates: "[fifo @ 0x…] Error opening rtmp://…: Error number -138 occurred"
    /// and "Slave muxer #0 failed: …, continuing with 1/2 slaves." (the latter doesn't name the
    /// slave; the only other slave is the local mp4, whose death warrants the same rebuild).
    /// </summary>
    public static bool IsRtmpSlaveFailureLine(string line) =>
        (line.Contains("Slave muxer #", StringComparison.Ordinal) &&
         line.Contains("failed", StringComparison.Ordinal)) ||
        (line.StartsWith("[fifo", StringComparison.Ordinal) &&
         line.Contains("rtmp://", StringComparison.OrdinalIgnoreCase) &&
         line.Contains("Error", StringComparison.Ordinal));
}
