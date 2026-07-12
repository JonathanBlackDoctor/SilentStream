using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

/// <summary>
/// Converts ffmpeg's CUMULATIVE stats counters into windowed recent rates. ffmpeg's own
/// fps=/bitrate= fields are session-cumulative averages (total frames / total elapsed), so on a
/// long session a sudden encoder sag or uplink stall barely moves them — useless for both the
/// adaptive-quality triggers and honest UI display (확장계획서_적응형송출품질 §5.1). Deltas of
/// the frame=/size= counters between consecutive stats ticks recover the true recent rate.
/// One instance per encoder session (a fresh ffmpeg restarts its counters).
/// </summary>
public sealed class MetricsWindower
{
    private MetricsSnapshot? _previous;

    /// <summary>
    /// Returns <paramref name="raw"/> with Fps/UploadBitrateKbps replaced by rates computed from
    /// counter deltas against the previous tick. The first tick, degenerate tick spacing, and
    /// absent counters (size=N/A under the live tee) pass the cumulative values through — at
    /// session start they equal the recent rate anyway.
    /// </summary>
    public MetricsSnapshot Apply(MetricsSnapshot raw)
    {
        var previous = _previous;
        _previous = raw;
        if (previous is null)
        {
            return raw;
        }
        var elapsed = (raw.TimestampUtc - previous.TimestampUtc).TotalSeconds;
        if (elapsed < 0.2)
        {
            return raw; // a burst of buffered lines — deltas would be noise
        }

        var result = raw;
        if (raw.FrameCount > 0 && previous.FrameCount > 0 && raw.FrameCount >= previous.FrameCount)
        {
            // Equal counters are meaningful: a starved/stalled encoder reads as 0 fps here even
            // while the cumulative average still looks healthy.
            result = result with { Fps = (raw.FrameCount - previous.FrameCount) / elapsed };
        }
        if (raw.OutputKBytes > 0 && previous.OutputKBytes > 0 && raw.OutputKBytes >= previous.OutputKBytes)
        {
            // KiB → kbit: ×1024×8/1000. Live tee sessions report size=N/A (counters stay 0), so
            // this branch effectively serves recording-only sessions and the UI display.
            result = result with
            {
                UploadBitrateKbps = (raw.OutputKBytes - previous.OutputKBytes) * 8.192 / elapsed
            };
        }
        return result;
    }
}
