using System.Globalization;
using System.Security.Cryptography;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Media;

public interface IVodMediaValidator
{
    Task<VerifiedRecordingCoverage?> VerifyAsync(RecordingSession source, DateTime start, DateTime end,
        string outputPath, CancellationToken ct);
    Task<bool> VerifyJoinedAsync(string outputPath, TimeSpan duration, CancellationToken ct);
}

/// <summary>Decode video (not container/audio duration), reject decoder errors, and match every
/// requested source frame inside the output. A duration-only check cannot detect a shifted
/// stream-copy start. No GOP-sized shortfall allowance: uncertain coverage is retried/preserved.</summary>
public sealed class VodMediaValidator(IProcessRunner runner) : IVodMediaValidator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    public async Task<VerifiedRecordingCoverage?> VerifyAsync(RecordingSession source, DateTime start,
        DateTime end, string outputPath, CancellationToken ct)
    {
        using var stableOutput = File.OpenRead(outputPath); // prohibit changes while comparing/hash-stamping
        if (stableOutput.Length < 2048 || end <= start) return null;
        var from = (start - source.StartLocal).TotalSeconds;
        var until = (end - source.StartLocal).TotalSeconds;
        // Decode the same keyframe preroll retained by the stream copy. Accurate input seek
        // would discard the frame straddling the requested start, leaving its prefix unproven.
        var sourceTrace = await TraceAsync($"-copyts -noaccurate_seek -ss {Number(from)} -i \"{source.FilePath}\" -to {Number(until)}", ct);
        var outputTrace = await TraceAsync($"-copyts -i \"{outputPath}\"", ct);
        var requestedStart = (start - source.StartLocal).Ticks;
        var requestedEnd = (end - source.StartLocal).Ticks;
        if (sourceTrace is null || outputTrace is null ||
            !sourceTrace.ContainsRequestedFrames(outputTrace, requestedStart, requestedEnd)) return null;
        // Absolute source PTS anchors coverage. Ceil start / floor end so tick conversion never
        // grants permission for an unobserved interval; no arbitrary 1-2 second tolerance.
        var verifiedStart = source.StartLocal.AddTicks(sourceTrace.StartTicks);
        var verifiedEnd = source.StartLocal.AddTicks(sourceTrace.EndTicks);
        if (verifiedEnd < end) return null;
        // AAC priming / a non-frame-aligned seek can place the first selected frame just after
        // the requested boundary. Accept the clip, but NEVER certify that unobserved prefix.
        // A full missing frame is still a failed extraction; this is not a duration tolerance.
        if (verifiedStart > start && verifiedStart.Ticks - start.Ticks >= sourceTrace.FirstFrameDurationTicks) return null;
        return new(Path.GetFullPath(source.FilePath), source.StartLocal, verifiedStart > start ? verifiedStart : start,
            end, stableOutput.Length,
            Convert.ToHexString(await SHA256.HashDataAsync(stableOutput, ct)));
    }

    public async Task<bool> VerifyJoinedAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var trace = await TraceAsync($"-copyts -i \"{outputPath}\"", ct);
        return trace is not null && trace.EndTicks - trace.StartTicks >= duration.Ticks;
    }

    private async Task<FrameTrace?> TraceAsync(string input, CancellationToken ct)
    {
        var (code, text) = await runner.RunAsync(FfmpegLocator.Resolve(),
            // Preserve the demuxer's timebase: rounding AAC-shifted PTS to 1/fps can change
            // frame durations/counts at a cut boundary and falsely reject healthy media.
            $"-hide_banner -v error -xerror {input} -map 0:v:0 -an -enc_time_base demux -fps_mode passthrough -f framemd5 -", Timeout, ct)
            .ConfigureAwait(false);
        return code == 0 ? FrameTrace.Parse(text) : null;
    }
    private static string Number(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

    internal sealed record Frame(long Pts, long Duration, long Size, string Hash);
    internal sealed record FrameTrace(long Numerator, long Denominator, IReadOnlyList<Frame> Frames)
    {
        private decimal Ticks(long pts) => (decimal)pts * Numerator * TimeSpan.TicksPerSecond / Denominator;
        public long StartTicks => checked((long)decimal.Ceiling(Ticks(Frames[0].Pts)));
        public long EndTicks => checked((long)decimal.Floor(Ticks(Frames[^1].Pts + Frames[^1].Duration)));
        public decimal FirstFrameDurationTicks => Ticks(Frames[0].Duration);
        public bool ContainsRequestedFrames(FrameTrace output, long requestedStart, long requestedEnd)
        {
            if (Frames.Count > output.Frames.Count) return false;
            for (var offset = 0; offset <= output.Frames.Count - Frames.Count; offset++)
            {
                var anchor = output.Frames[offset].Pts;
                // Map output PTS to source PTS using the matched frame, never container duration.
                decimal SourceTicks(long outputPts) => Ticks(Frames[0].Pts) + output.Ticks(outputPts - anchor);
                if (offset > 0 && SourceTicks(output.Frames[offset - 1].Pts + output.Frames[offset - 1].Duration) > requestedStart) continue;
                var after = offset + Frames.Count;
                if (after < output.Frames.Count && SourceTicks(output.Frames[after].Pts) < requestedEnd) continue;
                var matches = true;
                for (var i = 0; i < Frames.Count; i++)
                {
                    var expected = Frames[i]; var actual = output.Frames[offset + i];
                    if (expected.Size != actual.Size || expected.Hash != actual.Hash ||
                        Math.Abs(Ticks(expected.Pts - Frames[0].Pts) - output.Ticks(actual.Pts - anchor)) >= 1 ||
                        Math.Abs(Ticks(expected.Duration) - output.Ticks(actual.Duration)) >= 1)
                    { matches = false; break; }
                }
                if (matches) return true;
            }
            return false;
        }

        public static FrameTrace? Parse(string text)
        {
            long num = 0, den = 0; var frames = new List<Frame>();
            foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("#tb 0: ", StringComparison.Ordinal))
                {
                    var parts = line[7..].Split('/');
                    if (parts.Length != 2 || !long.TryParse(parts[0], out num) || !long.TryParse(parts[1], out den) || num <= 0 || den <= 0) return null;
                }
                else if (line.StartsWith('#')) continue;
                else
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    // Any stderr decoder error is non-frame text and invalidates even exit 0.
                    if (parts.Length != 6 || parts[0] != "0" || !long.TryParse(parts[2], out var pts) ||
                        !long.TryParse(parts[3], out var duration) || duration <= 0 ||
                        !long.TryParse(parts[4], out var size) || size <= 0 || parts[5].Length != 32 ||
                        !parts[5].All(Uri.IsHexDigit)) return null;
                    if (frames.Count > 0 && pts != frames[^1].Pts + frames[^1].Duration) return null;
                    frames.Add(new(pts, duration, size, parts[5]));
                }
            }
            return num > 0 && den > 0 && frames.Count > 0 ? new(num, den, frames) : null;
        }
    }
}
