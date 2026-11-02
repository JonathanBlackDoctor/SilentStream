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
/// output frame against the requested source window. A duration-only check cannot detect a shifted
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
        var sourceTrace = await TraceAsync($"-copyts -ss {Number(from)} -i \"{source.FilePath}\" -to {Number(until)}", ct);
        var outputTrace = await TraceAsync($"-copyts -i \"{outputPath}\"", ct);
        if (sourceTrace is null || outputTrace is null || !sourceTrace.SameFrames(outputTrace)) return null;
        // Absolute source PTS anchors coverage. Ceil start / floor end so tick conversion never
        // grants permission for an unobserved interval; no arbitrary 1-2 second tolerance.
        var verifiedStart = source.StartLocal.AddTicks(sourceTrace.StartTicks);
        var verifiedEnd = source.StartLocal.AddTicks(sourceTrace.EndTicks);
        if (verifiedStart > start || verifiedEnd < end) return null;
        return new(Path.GetFullPath(source.FilePath), source.StartLocal, start, end, stableOutput.Length,
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
            $"-hide_banner -v error -xerror {input} -map 0:v:0 -an -fps_mode passthrough -f framemd5 -", Timeout, ct)
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
        public bool SameFrames(FrameTrace other) => Frames.Count == other.Frames.Count &&
            Frames.Zip(other.Frames).All(pair => pair.First.Size == pair.Second.Size &&
                pair.First.Hash == pair.Second.Hash &&
                Math.Abs(Ticks(pair.First.Pts - Frames[0].Pts) - other.Ticks(pair.Second.Pts - other.Frames[0].Pts)) < 1 &&
                Math.Abs(Ticks(pair.First.Duration) - other.Ticks(pair.Second.Duration)) < 1);

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
