using System.Globalization;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class VodSegmentServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-vod-").FullName;
    private readonly string _outputDir;
    private readonly FakeRunner _runner = new();

    public VodSegmentServiceTests()
    {
        _outputDir = Path.Combine(_dir, "vod");
    }

    private VodSegmentService Create(RecordingSession? session) =>
        new(new FakeSessionInfo(session), _runner, new LogService(), _outputDir);

    private string MakeSessionFile()
    {
        var path = Path.Combine(_dir, "MediaCaptureHelper_REC_2026-06-14_0859.mp4");
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    [Fact]
    public async Task Computes_file_relative_offsets_and_returns_the_cut_path()
    {
        var sessionFile = MakeSessionFile();
        var sessionStart = new DateTime(2026, 6, 14, 8, 59, 0);
        var service = Create(new RecordingSession(sessionFile, sessionStart));

        // Period 1: 09:00:00–09:50:00. Session started at 08:59:00 → ss=60s, duration=3000s.
        var period = new PeriodBoundary(new DateOnly(2026, 6, 14), 1,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

        var result = await service.ExtractPeriodAsync(period, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1교시_2026-06-14.mp4", Path.GetFileName(result));
        Assert.Equal(60d, ParseArg(_runner.LastArgs, "-ss"), 3);
        Assert.Equal(3000d, ParseArg(_runner.LastArgs, "-t"), 3);
        Assert.Contains("-c copy", _runner.LastArgs);
        Assert.Contains(sessionFile, _runner.LastArgs);
    }

    [Fact]
    public async Task Clamps_start_to_zero_when_period_precedes_recording_start()
    {
        var sessionFile = MakeSessionFile();
        // Recording started at 09:10; period 1 began at 09:00 (before recording).
        var service = Create(new RecordingSession(sessionFile, new DateTime(2026, 6, 14, 9, 10, 0)));
        var period = new PeriodBoundary(new DateOnly(2026, 6, 14), 1,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

        var result = await service.ExtractPeriodAsync(period, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0d, ParseArg(_runner.LastArgs, "-ss"), 3);      // clamped
        Assert.Equal(2400d, ParseArg(_runner.LastArgs, "-t"), 3);    // 09:10→09:50 only
    }

    [Fact]
    public async Task Returns_null_when_no_session_is_recording()
    {
        var service = Create(session: null);
        var period = new PeriodBoundary(new DateOnly(2026, 6, 14), 1,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

        Assert.Null(await service.ExtractPeriodAsync(period, CancellationToken.None));
        Assert.False(_runner.WasCalled); // never shells out to ffmpeg
    }

    [Fact]
    public async Task Returns_null_when_window_is_empty()
    {
        var sessionFile = MakeSessionFile();
        // Recording started after the period already ended → nothing to extract.
        var service = Create(new RecordingSession(sessionFile, new DateTime(2026, 6, 14, 10, 0, 0)));
        var period = new PeriodBoundary(new DateOnly(2026, 6, 14), 1,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

        Assert.Null(await service.ExtractPeriodAsync(period, CancellationToken.None));
        Assert.False(_runner.WasCalled);
    }

    [Fact]
    public async Task Range_cut_uses_the_snapshot_session_and_merged_label_filename()
    {
        var sessionFile = MakeSessionFile();
        var sessionStart = new DateTime(2026, 6, 14, 8, 59, 0);
        // Current is null on purpose: the approval path passes its own session snapshot, so the
        // cut must work after the live session ended or was replaced (e.g. app restart).
        var service = Create(session: null);

        var result = await service.ExtractRangeAsync(
            new RecordingSession(sessionFile, sessionStart),
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 10, 50, 0),
            "1~2교시", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1~2교시_2026-06-14.mp4", Path.GetFileName(result));
        Assert.Equal(60d, ParseArg(_runner.LastArgs, "-ss"), 3);
        Assert.Equal(6600d, ParseArg(_runner.LastArgs, "-t"), 3); // 09:00→10:50
        Assert.Contains(sessionFile, _runner.LastArgs);
    }

    [Fact]
    public async Task Range_cut_returns_null_when_the_snapshot_file_is_gone()
    {
        // A long-pending split can outlive its session file (retention/disk cleanup).
        var service = Create(session: null);

        Assert.Null(await service.ExtractRangeAsync(
            new RecordingSession(Path.Combine(_dir, "deleted.mp4"), new DateTime(2026, 6, 14, 8, 59, 0)),
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0),
            "1교시", CancellationToken.None));
        Assert.False(_runner.WasCalled);
    }

    [Fact]
    public async Task Returns_null_and_does_not_crash_when_ffmpeg_fails()
    {
        var sessionFile = MakeSessionFile();
        _runner.SimulateFailure = true;
        var service = Create(new RecordingSession(sessionFile, new DateTime(2026, 6, 14, 8, 59, 0)));
        var period = new PeriodBoundary(new DateOnly(2026, 6, 14), 1,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

        Assert.Null(await service.ExtractPeriodAsync(period, CancellationToken.None));
    }

    private static double ParseArg(string args, string flag)
    {
        var m = Regex.Match(args, Regex.Escape(flag) + @"\s+([0-9.]+)");
        Assert.True(m.Success, $"flag {flag} not found in: {args}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeSessionInfo(RecordingSession? current) : IRecordingSessionInfo
    {
        public RecordingSession? Current { get; } = current;
    }

    /// <summary>Fake ffmpeg: records args and, unless told to fail, creates the output file.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public string LastArgs = string.Empty;
        public bool WasCalled;
        public bool SimulateFailure;

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            WasCalled = true;
            LastArgs = arguments;
            if (SimulateFailure)
            {
                return Task.FromResult((1, "Conversion failed!"));
            }

            // Output path is the final quoted token.
            var quoted = Regex.Matches(arguments, "\"([^\"]+)\"");
            var outputPath = quoted[^1].Groups[1].Value;
            File.WriteAllBytes(outputPath, new byte[2048]);
            return Task.FromResult((0, "ok"));
        }
    }
}
