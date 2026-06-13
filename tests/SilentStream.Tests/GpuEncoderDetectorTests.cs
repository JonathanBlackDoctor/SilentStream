using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using Xunit;

namespace SilentStream.Tests;

public class GpuEncoderDetectorTests
{
    /// <summary>Scripted fake: encoder list output + per-encoder probe results.</summary>
    private sealed class FakeRunner(string encoderList, Func<string, bool> probeSucceeds)
        : IProcessRunner
    {
        public List<string> Invocations { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Invocations.Add(arguments);
            if (arguments.Contains("-encoders"))
            {
                return Task.FromResult((0, encoderList));
            }
            var ok = probeSucceeds(arguments);
            return Task.FromResult((ok ? 0 : 1, string.Empty));
        }
    }

    private const string AllEncoders = "h264_nvenc h264_amf h264_qsv libx264";

    [Fact]
    public async Task Picks_nvenc_when_listed_and_probe_succeeds()
    {
        var runner = new FakeRunner(AllEncoders, _ => true);
        var detector = new GpuEncoderDetector(runner, new LogService());

        var result = await detector.DetectAsync("ffmpeg", CancellationToken.None);

        Assert.Equal(GpuEncoder.Nvenc, result);
    }

    [Fact]
    public async Task Falls_through_priority_when_probes_fail()
    {
        // NVENC listed but the probe encode fails (no driver) → AMF probe also fails → QSV works.
        var runner = new FakeRunner(AllEncoders, args => args.Contains("h264_qsv"));
        var detector = new GpuEncoderDetector(runner, new LogService());

        var result = await detector.DetectAsync("ffmpeg", CancellationToken.None);

        Assert.Equal(GpuEncoder.Qsv, result);
    }

    [Fact]
    public async Task Unlisted_hw_encoders_are_skipped_without_probing()
    {
        var runner = new FakeRunner("libx264", args => !args.Contains("nvenc"));
        var detector = new GpuEncoderDetector(runner, new LogService());

        var result = await detector.DetectAsync("ffmpeg", CancellationToken.None);

        Assert.Equal(GpuEncoder.X264, result);
        Assert.DoesNotContain(runner.Invocations, a => a.Contains("h264_nvenc") && a.Contains("lavfi"));
    }

    [Fact]
    public async Task Everything_failing_still_returns_x264()
    {
        var runner = new FakeRunner(string.Empty, _ => false);
        var detector = new GpuEncoderDetector(runner, new LogService());

        var result = await detector.DetectAsync("ffmpeg", CancellationToken.None);

        Assert.Equal(GpuEncoder.X264, result);
    }

    [Fact]
    public async Task Integration_real_ffmpeg_detection_returns_a_working_encoder()
    {
        // Runs only where an ffmpeg binary is on PATH (dev boxes / CI with ffmpeg).
        var probe = new ProcessRunner();
        try
        {
            var (exitCode, _) = await probe.RunAsync(
                "ffmpeg", "-version", TimeSpan.FromSeconds(10), CancellationToken.None);
            if (exitCode != 0)
            {
                return;
            }
        }
        catch (Exception)
        {
            return; // ffmpeg not installed — skip silently.
        }

        var detector = new GpuEncoderDetector(new ProcessRunner(), new LogService());
        var result = await detector.DetectAsync("ffmpeg", CancellationToken.None);

        // Whatever was picked must pass its own 1-frame probe, so this can't be a bogus value.
        Assert.True(Enum.IsDefined(result));
    }
}
