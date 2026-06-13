using System.Globalization;

namespace SilentStream.Core.Media;

/// <summary>
/// Everything needed to compose one encoding session's FFmpeg command line.
/// </summary>
/// <param name="Width">Capture width in pixels.</param>
/// <param name="Height">Capture height in pixels.</param>
/// <param name="Fps">Capture frame rate.</param>
/// <param name="Encoder">Selected H.264 encoder family.</param>
/// <param name="VideoBitrateKbps">Target video bitrate (kbps).</param>
/// <param name="AudioBitrateKbps">Target audio bitrate (kbps).</param>
/// <param name="RtmpUrl">Full RTMP ingest URL including stream key (null/empty = recording only).</param>
/// <param name="RecordingFilePath">Session mp4 path (null/empty = streaming only).</param>
/// <param name="AudioPipeName">Named-pipe path the mixer writes s16le PCM into.</param>
/// <param name="SampleRate">PCM sample rate.</param>
/// <param name="Channels">PCM channel count.</param>
/// <param name="ResourceLimit">"25" | "50" | "75" | "none" (plan §3.5).</param>
public sealed record EncoderSessionSpec(
    int Width,
    int Height,
    double Fps,
    GpuEncoder Encoder,
    int VideoBitrateKbps,
    int AudioBitrateKbps,
    string? RtmpUrl,
    string? RecordingFilePath,
    string AudioPipeName,
    int SampleRate = 48000,
    int Channels = 2,
    string ResourceLimit = "none");

/// <summary>
/// Builds the FFmpeg command line: raw BGRA video on stdin + PCM audio from a named
/// pipe, encoded once, then fanned out via the tee muxer to RTMP (onfail=ignore so a
/// network drop never kills the local recording) and a fragmented mp4 (plan §3.6).
/// </summary>
public static class FfmpegArgumentsBuilder
{
    public static string Build(EncoderSessionSpec spec) =>
        Build(spec, Environment.ProcessorCount);

    /// <summary>Overload with explicit core count for deterministic tests.</summary>
    public static string Build(EncoderSessionSpec spec, int processorCount)
    {
        var hasRtmp = !string.IsNullOrEmpty(spec.RtmpUrl);
        var hasFile = !string.IsNullOrEmpty(spec.RecordingFilePath);
        if (!hasRtmp && !hasFile)
        {
            throw new ArgumentException("At least one of RtmpUrl/RecordingFilePath is required.", nameof(spec));
        }

        var fps = spec.Fps.ToString(CultureInfo.InvariantCulture);
        var gop = (int)Math.Round(spec.Fps * 2); // 2s keyframe interval (YouTube recommendation)

        var args = new List<string>
        {
            "-hide_banner", "-loglevel warning", "-stats_period 2",
            // input 0: raw BGRA frames pushed by the capture source via stdin
            "-f rawvideo", "-pix_fmt bgra",
            $"-s {spec.Width}x{spec.Height}", $"-framerate {fps}",
            "-i pipe:0",
            // input 1: mixed PCM from the audio mixer via named pipe
            "-f s16le", $"-ar {spec.SampleRate}", $"-ac {spec.Channels}",
            $"-i \"{spec.AudioPipeName}\"",
            // video encode
            $"-c:v {spec.Encoder.ToFfmpegName()}",
            $"-b:v {spec.VideoBitrateKbps}k",
            $"-maxrate {spec.VideoBitrateKbps}k",
            $"-bufsize {spec.VideoBitrateKbps * 2}k",
            $"-g {gop}",
            "-pix_fmt yuv420p"
        };

        args.AddRange(GetEncoderTuning(spec.Encoder, spec.ResourceLimit, processorCount));

        args.Add($"-c:a aac -b:a {spec.AudioBitrateKbps}k");

        // The tee muxer cannot propagate per-container needs to the encoder, so
        // extradata must be in-band globally — without this the mp4 slave is unplayable
        // (verified against ffmpeg 6.1: "No start code is found").
        args.Add("-flags +global_header");

        if (hasRtmp && hasFile)
        {
            args.Add("-f tee -map 0:v -map 1:a");
            args.Add($"\"[f=flv:onfail=ignore]{spec.RtmpUrl}|" +
                     $"[f=mp4:movflags=+frag_keyframe+empty_moov]{TeeEscape(spec.RecordingFilePath!)}\"");
        }
        else if (hasRtmp)
        {
            args.Add($"-map 0:v -map 1:a -f flv \"{spec.RtmpUrl}\"");
        }
        else
        {
            args.Add("-map 0:v -map 1:a -movflags +frag_keyframe+empty_moov " +
                     $"-f mp4 \"{spec.RecordingFilePath}\"");
        }

        return string.Join(' ', args);
    }

    /// <summary>
    /// Approximate resource cap (plan §3.5): x264 threads + preset; HW encoders only
    /// vary preset since the GPU does the heavy lifting.
    /// </summary>
    private static IEnumerable<string> GetEncoderTuning(
        GpuEncoder encoder, string resourceLimit, int processorCount)
    {
        var fraction = resourceLimit switch
        {
            "25" => 0.25,
            "50" => 0.50,
            "75" => 0.75,
            _ => 1.0
        };

        if (encoder == GpuEncoder.X264)
        {
            var threads = Math.Max(1, (int)Math.Floor(processorCount * fraction));
            yield return $"-threads {threads}";
            yield return fraction switch
            {
                <= 0.25 => "-preset ultrafast",
                <= 0.50 => "-preset superfast",
                <= 0.75 => "-preset veryfast",
                _ => "-preset faster"
            };
            yield return "-tune zerolatency";
        }
        else if (encoder == GpuEncoder.Nvenc)
        {
            yield return fraction <= 0.5 ? "-preset p2" : "-preset p4";
            yield return "-rc cbr";
        }
        else if (encoder == GpuEncoder.Qsv)
        {
            yield return fraction <= 0.5 ? "-preset veryfast" : "-preset medium";
        }
        else if (encoder == GpuEncoder.Amf)
        {
            yield return fraction <= 0.5 ? "-quality speed" : "-quality balanced";
        }
    }

    /// <summary>
    /// Escapes a path for use inside a tee output entry: '\' → '/', and any
    /// "'", ':' (after the drive letter), '[', ']' or '|' would corrupt the spec.
    /// Windows drive colons are accepted by FFmpeg's Windows builds.
    /// </summary>
    private static string TeeEscape(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains('|') || normalized.Contains('[') || normalized.Contains(']'))
        {
            throw new ArgumentException($"녹화 경로에 사용할 수 없는 문자가 있습니다: {path}");
        }
        return normalized;
    }
}
