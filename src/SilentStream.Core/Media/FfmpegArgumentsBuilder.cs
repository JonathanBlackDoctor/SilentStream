using System.Globalization;
using SilentStream.Core.Models;

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
/// <param name="AudioFilters">Master-bus filters (afftdn/acompressor/alimiter/volume); null = none.</param>
/// <param name="OutputWidth">Encode/scale target width; 0 = same as <paramref name="Width"/> (no scale).</param>
/// <param name="OutputHeight">Encode/scale target height; 0 = same as <paramref name="Height"/>.</param>
/// <param name="OutputFps">Output frame rate; 0 = same as <paramref name="Fps"/> (no frame drop).</param>
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
    string ResourceLimit = "none",
    AudioFilterSettings? AudioFilters = null,
    int OutputWidth = 0,
    int OutputHeight = 0,
    double OutputFps = 0)
{
    /// <summary>Effective encode width (output override or input).</summary>
    public int EffectiveWidth => OutputWidth > 0 ? OutputWidth : Width;

    /// <summary>Effective encode height (output override or input).</summary>
    public int EffectiveHeight => OutputHeight > 0 ? OutputHeight : Height;

    /// <summary>Effective output frame rate (override or input).</summary>
    public double EffectiveFps => OutputFps > 0 ? OutputFps : Fps;

    /// <summary>True when the frames must be rescaled before encoding.</summary>
    public bool NeedsScale => EffectiveWidth != Width || EffectiveHeight != Height;
}

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

        var inputFps = spec.Fps.ToString(CultureInfo.InvariantCulture);
        var gop = (int)Math.Round(spec.EffectiveFps * 2); // 2s keyframe interval (YouTube recommendation)

        var args = new List<string>
        {
            // -stats must be EXPLICIT: with a redirected (non-tty) stderr at -loglevel warning,
            // ffmpeg suppresses the periodic report entirely (실측, 8.1) — without this flag the
            // app never receives progress lines, so the stall watchdog and all metrics starve.
            "-hide_banner", "-loglevel warning", "-stats", "-stats_period 2",
            // input 0: raw BGRA frames pushed by the capture source via stdin
            "-f rawvideo", "-pix_fmt bgra",
            $"-s {spec.Width}x{spec.Height}", $"-framerate {inputFps}",
            "-i pipe:0",
            // input 1: mixed PCM from the audio mixer via named pipe
            "-f s16le", $"-ar {spec.SampleRate}", $"-ac {spec.Channels}",
            $"-i \"{spec.AudioPipeName}\""
        };

        // Optional downscale + output frame-rate reduction (manual resolution/fps options). Emitted
        // only when they differ from the captured input, so the default (source) path is unchanged.
        if (spec.NeedsScale)
        {
            // Preserve aspect (downscale to fit) then pad to the exact target with black bars, so a
            // mismatched monitor/region aspect letterboxes instead of stretching.
            var w = spec.EffectiveWidth;
            var h = spec.EffectiveHeight;
            args.Add($"-vf \"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2\"");
        }
        if (Math.Abs(spec.EffectiveFps - spec.Fps) > 0.01)
        {
            args.Add($"-r {spec.EffectiveFps.ToString(CultureInfo.InvariantCulture)}");
        }

        args.AddRange(new[]
        {
            // video encode
            $"-c:v {spec.Encoder.ToFfmpegName()}",
            $"-b:v {spec.VideoBitrateKbps}k",
            $"-maxrate {spec.VideoBitrateKbps}k",
            $"-bufsize {spec.VideoBitrateKbps * 2}k",
            $"-g {gop}",
            "-pix_fmt yuv420p"
        });

        args.AddRange(GetEncoderTuning(spec.Encoder, spec.ResourceLimit, processorCount));

        // Master-bus audio post-processing (denoise/compressor/limiter/gain). Applied here, at
        // session start, so it survives the tee fan-out; realtime per-source gain/mute/gate live
        // in the WASAPI mixer instead (plan §3.4). Emitted only when something is enabled, so the
        // default (no filters) leaves the command line byte-for-byte unchanged.
        var audioFilter = BuildAudioFilterChain(spec.AudioFilters);
        if (audioFilter.Length > 0)
        {
            args.Add($"-af \"{audioFilter}\"");
        }

        args.Add($"-c:a aac -b:a {spec.AudioBitrateKbps}k");

        // The tee muxer cannot propagate per-container needs to the encoder, so
        // extradata must be in-band globally — without this the mp4 slave is unplayable
        // (verified against ffmpeg 6.1: "No start code is found").
        args.Add("-flags +global_header");

        if (hasRtmp && hasFile)
        {
            // use_fifo gives each slave its own thread + queue so a blocked/slow RTMP slave
            // (e.g. YouTube not yet consuming the ingest) cannot starve the local mp4 slave —
            // the tee otherwise writes slaves synchronously on one thread, so an RTMP stall
            // freezes the recording at 0 bytes (plan §3.6/§4.1: recording independent of RTMP).
            // drop_pkts_on_overflow keeps a backed-up RTMP from blocking; the mp4 slave drains
            // to local disk far faster than the queue fills, so the recording isn't dropped.
            args.Add("-f tee -use_fifo 1 -fifo_options queue_size=120:drop_pkts_on_overflow=1 -map 0:v -map 1:a");
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
    /// Composes the FFmpeg <c>-af</c> filter chain from the master-bus settings: spectral
    /// denoise (afftdn) → compressor (acompressor) → brick-wall limiter (alimiter) → make-up
    /// gain (volume). Returns an empty string when nothing is enabled. No spaces are emitted so
    /// the chain is a single shell token even before the surrounding quotes.
    /// </summary>
    internal static string BuildAudioFilterChain(AudioFilterSettings? filters)
    {
        if (filters is null || !filters.HasAny)
        {
            return string.Empty;
        }

        var chain = new List<string>();
        if (filters.NoiseSuppressionEnabled)
        {
            var nr = Math.Clamp(filters.NoiseSuppressionDb, 1, 97);
            chain.Add($"afftdn=nr={nr.ToString(CultureInfo.InvariantCulture)}:nf=-25");
        }
        if (filters.CompressorEnabled)
        {
            // Gentle speech compression: ~3:1 above -18 dBFS with fast-ish attack/release.
            chain.Add("acompressor=threshold=-18dB:ratio=3:attack=20:release=250");
        }
        if (filters.LimiterEnabled)
        {
            // Brick-wall just below full scale to stop clipping on peaks.
            chain.Add("alimiter=limit=0.95");
        }
        if (Math.Abs(filters.MasterGainDb) > 0.01)
        {
            var db = filters.MasterGainDb.ToString("0.##", CultureInfo.InvariantCulture);
            chain.Add($"volume={db}dB");
        }

        return string.Join(',', chain);
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
