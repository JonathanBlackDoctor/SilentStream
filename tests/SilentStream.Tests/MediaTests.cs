using SilentStream.Core.Media;
using Xunit;

namespace SilentStream.Tests;

public class BitrateMapperTests
{
    [Theory]
    [InlineData(1080, 60, 9000)]
    [InlineData(1080, 30, 6000)]
    [InlineData(1440, 60, 9000)] // above-1080p falls into the highest bucket
    [InlineData(720, 30, 3500)]
    [InlineData(480, 30, 2500)]
    public void Maps_resolution_and_fps_to_youtube_recommended_bitrate(
        int height, double fps, int expectedKbps)
    {
        Assert.Equal(expectedKbps, BitrateMapper.GetVideoBitrateKbps(height, fps));
    }
}

public class FfmpegArgumentsBuilderTests
{
    private static EncoderSessionSpec Spec(
        string? rtmp = "rtmp://a.rtmp.youtube.com/live2/key",
        string? file = @"C:\Users\me\Videos\MediaCaptureHelper\MediaCaptureHelper_REC_2026-06-12_0930.mp4",
        GpuEncoder encoder = GpuEncoder.Nvenc,
        string resourceLimit = "none") =>
        new(1920, 1080, 30, encoder, 6000, 160, rtmp, file,
            @"\\.\pipe\mediacapturehelper_audio", ResourceLimit: resourceLimit);

    [Fact]
    public void Tee_output_carries_rtmp_with_onfail_ignore_and_fragmented_mp4()
    {
        var args = FfmpegArgumentsBuilder.Build(Spec(), processorCount: 8);

        Assert.Contains("-f tee", args);
        // Required for tee: without global headers the mp4 slave is corrupt.
        Assert.Contains("-flags +global_header", args);
        Assert.Contains("[f=flv:onfail=ignore]rtmp://a.rtmp.youtube.com/live2/key", args);
        Assert.Contains("[f=mp4:movflags=+frag_keyframe+empty_moov]", args);
        // Recording must not die with RTMP: file output sits after the | separator.
        Assert.Matches(@"onfail=ignore\].*\|\[f=mp4", args);
        Assert.Contains("MediaCaptureHelper_REC_2026-06-12_0930.mp4", args);
    }

    [Fact]
    public void Video_input_is_raw_bgra_on_stdin_and_audio_is_pcm_named_pipe()
    {
        var args = FfmpegArgumentsBuilder.Build(Spec(), processorCount: 8);

        Assert.Contains("-f rawvideo", args);
        Assert.Contains("-pix_fmt bgra", args);
        Assert.Contains("-s 1920x1080", args);
        Assert.Contains("-i pipe:0", args);
        Assert.Contains("-f s16le", args);
        Assert.Contains(@"\\.\pipe\mediacapturehelper_audio", args);
        Assert.Contains("-c:v h264_nvenc", args);
        Assert.Contains("-b:v 6000k", args);
        Assert.Contains("-c:a aac -b:a 160k", args);
    }

    [Fact]
    public void Recording_only_mode_skips_tee_and_rtmp()
    {
        var args = FfmpegArgumentsBuilder.Build(Spec(rtmp: null), processorCount: 8);

        Assert.DoesNotContain("-f tee", args);
        Assert.DoesNotContain("rtmp://", args);
        Assert.Contains("-f mp4", args);
        Assert.Contains("+frag_keyframe+empty_moov", args);
    }

    [Theory]
    [InlineData("25", 8, "-threads 2", "-preset ultrafast")]
    [InlineData("50", 8, "-threads 4", "-preset superfast")]
    [InlineData("75", 8, "-threads 6", "-preset veryfast")]
    [InlineData("none", 8, "-threads 8", "-preset faster")]
    public void X264_resource_limit_caps_threads_and_relaxes_preset(
        string limit, int cores, string expectedThreads, string expectedPreset)
    {
        var args = FfmpegArgumentsBuilder.Build(
            Spec(encoder: GpuEncoder.X264, resourceLimit: limit), cores);

        Assert.Contains(expectedThreads, args);
        Assert.Contains(expectedPreset, args);
    }

    [Fact]
    public void Backslashes_in_recording_path_are_normalized_for_tee()
    {
        var args = FfmpegArgumentsBuilder.Build(Spec(), processorCount: 8);
        Assert.Contains("C:/Users/me/Videos/MediaCaptureHelper/", args);
    }

    [Fact]
    public void Requires_at_least_one_output()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentsBuilder.Build(Spec(rtmp: null, file: null), 8));
    }
}

public class FfmpegProgressParserTests
{
    [Fact]
    public void Parses_fps_and_bitrate_from_stats_line()
    {
        var line = "frame= 1234 fps= 30 q=18.0 size=  10240kB time=00:00:41.13 bitrate=6021.3kbits/s speed=1x";
        var metrics = FfmpegProgressParser.TryParse(line, DateTime.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal(30, metrics!.Fps);
        Assert.Equal(6021.3, metrics.UploadBitrateKbps);
        Assert.Equal(1234, metrics.FrameCount);
        Assert.Equal(10240, metrics.OutputKBytes); // legacy "kB" suffix (1024-byte units too)
    }

    [Fact]
    public void Parses_cumulative_counters_from_a_real_81_recording_line()
    {
        // 실측 ffmpeg 8.1 (recording-only): KiB suffix, elapsed= field.
        var line = "frame=  180 fps=0.0 q=-1.0 Lsize=     373KiB time=00:00:06.00 bitrate= 509.3kbits/s speed= 101x elapsed=0:00:00.05";
        var metrics = FfmpegProgressParser.TryParse(line, DateTime.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal(180, metrics!.FrameCount);
        Assert.Equal(373, metrics.OutputKBytes); // Lsize= carries the same total
        Assert.Equal(509.3, metrics.UploadBitrateKbps);
    }

    [Fact]
    public void Tee_stats_with_na_size_still_parse_with_zero_counters()
    {
        // 실측 ffmpeg 8.1 under the tee muxer: size/bitrate are N/A there (2026-07-13).
        var line = "frame=   38 fps= 37 q=6.0 size=N/A time=00:00:01.26 bitrate=N/A speed=1.24x elapsed=0:00:01.02    ";
        var metrics = FfmpegProgressParser.TryParse(line, DateTime.UtcNow);

        Assert.NotNull(metrics);
        Assert.Equal(38, metrics!.FrameCount);
        Assert.Equal(0, metrics.OutputKBytes);
        Assert.Equal(0, metrics.UploadBitrateKbps);
        Assert.Equal(37, metrics.Fps);
    }

    [Fact]
    public void Non_stats_lines_return_null()
    {
        Assert.Null(FfmpegProgressParser.TryParse("Stream mapping:", DateTime.UtcNow));
    }
}

public class FfmpegStderrClassifierTests
{
    // Drop/failure fixtures are 실측 ffmpeg 8.1 output (dead-RTMP tee run, 2026-07-13) plus the
    // binary's own message template for the tee-level slave failure.
    [Theory]
    [InlineData("[fifo @ 000001bdf5e4d840] FIFO queue full", true)]
    [InlineData("frame=   38 fps= 37 q=6.0 size=N/A time=00:00:01.26 bitrate=N/A speed=1.24x", false)]
    [InlineData("[rtmp @ 000001bdf4c10100] Cannot open connection tcp://127.0.0.1:9?tcp_nodelay=0", false)]
    public void Detects_fifo_drop_lines(string line, bool expected) =>
        Assert.Equal(expected, FfmpegProgressParser.IsTeeDropLine(line));

    [Theory]
    [InlineData("[fifo @ 000001bdf5e4d840] Error opening rtmp://127.0.0.1:9/live/x: Error number -138 occurred", true)]
    [InlineData("[tee @ 000001bdf4c0e000] Slave muxer #0 failed: Error number -32 occurred, continuing with 1/2 slaves.", true)]
    [InlineData("[fifo @ 000001bdf5e4d840] FIFO queue full", false)]
    [InlineData("[fifo @ 000001bdf5e4d840] Error writing packet to C:/rec/session.mp4: I/O error", false)]
    [InlineData("frame=   38 fps= 37 q=6.0 size=N/A time=00:00:01.26 bitrate=N/A speed=1.24x", false)]
    public void Detects_rtmp_slave_failure_lines(string line, bool expected) =>
        Assert.Equal(expected, FfmpegProgressParser.IsRtmpSlaveFailureLine(line));
}

public class MetricsWindowerTests
{
    private static SilentStream.Core.Models.MetricsSnapshot Raw(
        double sec, long frame, long kib, double fps = 30, double kbps = 6000) =>
        new(kbps, fps, 0, -1, DateTime.UnixEpoch.AddSeconds(sec),
            FrameCount: frame, OutputKBytes: kib);

    [Fact]
    public void First_tick_passes_cumulative_values_through()
    {
        var windower = new MetricsWindower();

        var first = windower.Apply(Raw(0, 60, 100));

        Assert.Equal(30, first.Fps);
        Assert.Equal(6000, first.UploadBitrateKbps);
    }

    [Fact]
    public void Windows_fps_and_bitrate_from_counter_deltas()
    {
        var windower = new MetricsWindower();
        windower.Apply(Raw(0, 60, 1000));

        var second = windower.Apply(Raw(2, 120, 2000)); // +60 frames, +1000KiB over 2s

        Assert.Equal(30, second.Fps);
        Assert.Equal(1000 * 8.192 / 2, second.UploadBitrateKbps); // 4096 kbps
    }

    [Fact]
    public void Stalled_frame_counter_reads_zero_fps_even_when_cumulative_average_looks_healthy()
    {
        var windower = new MetricsWindower();
        windower.Apply(Raw(0, 7200, 0, fps: 60)); // 2 min in — cumulative average still 60

        var stalled = windower.Apply(Raw(2, 7200, 0, fps: 59)); // no new frames for 2s

        Assert.Equal(0, stalled.Fps); // the sag ffmpeg's own fps= field would hide for minutes
    }

    [Fact]
    public void Missing_size_counters_keep_ffmpegs_bitrate_value()
    {
        var windower = new MetricsWindower();
        windower.Apply(Raw(0, 60, 0, kbps: 0));

        var second = windower.Apply(Raw(2, 120, 0, kbps: 0)); // live tee: size=N/A → counters 0

        Assert.Equal(30, second.Fps); // fps still windowed from frame deltas
        Assert.Equal(0, second.UploadBitrateKbps);
    }

    [Fact]
    public void Degenerate_tick_spacing_passes_through()
    {
        var windower = new MetricsWindower();
        windower.Apply(Raw(0, 60, 100));

        var burst = windower.Apply(Raw(0.05, 61, 101, fps: 42)); // buffered-line burst

        Assert.Equal(42, burst.Fps); // cumulative value kept — no noisy micro-delta
    }
}
