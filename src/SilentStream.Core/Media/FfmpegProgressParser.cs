using System.Globalization;
using System.Text.RegularExpressions;
using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>
/// Parses FFmpeg's periodic stats lines
/// (e.g. "frame= 1234 fps= 30 ... bitrate=6021.3kbits/s ...") into metrics.
/// </summary>
public static partial class FfmpegProgressParser
{
    [GeneratedRegex(@"fps=\s*(?<fps>[\d.]+)")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"bitrate=\s*(?<rate>[\d.]+)\s*kbits/s")]
    private static partial Regex BitrateRegex();

    /// <summary>Returns a snapshot if the line carries stats, otherwise null.</summary>
    public static MetricsSnapshot? TryParse(string line, DateTime timestampUtc)
    {
        var fpsMatch = FpsRegex().Match(line);
        var rateMatch = BitrateRegex().Match(line);
        if (!fpsMatch.Success && !rateMatch.Success)
        {
            return null;
        }

        var fps = fpsMatch.Success
            ? double.Parse(fpsMatch.Groups["fps"].Value, CultureInfo.InvariantCulture)
            : 0;
        var rate = rateMatch.Success
            ? double.Parse(rateMatch.Groups["rate"].Value, CultureInfo.InvariantCulture)
            : 0;

        return new MetricsSnapshot(rate, fps, CpuPercent: 0, GpuPercent: -1, timestampUtc);
    }
}
