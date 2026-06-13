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
        string? file = @"C:\Users\me\Videos\SilentStream\SilentStream_REC_2026-06-12_0930.mp4",
        GpuEncoder encoder = GpuEncoder.Nvenc,
        string resourceLimit = "none") =>
        new(1920, 1080, 30, encoder, 6000, 160, rtmp, file,
            @"\\.\pipe\silentstream_audio", ResourceLimit: resourceLimit);

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
        Assert.Contains("SilentStream_REC_2026-06-12_0930.mp4", args);
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
        Assert.Contains(@"\\.\pipe\silentstream_audio", args);
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
        Assert.Contains("C:/Users/me/Videos/SilentStream/", args);
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
    }

    [Fact]
    public void Non_stats_lines_return_null()
    {
        Assert.Null(FfmpegProgressParser.TryParse("Stream mapping:", DateTime.UtcNow));
    }
}
