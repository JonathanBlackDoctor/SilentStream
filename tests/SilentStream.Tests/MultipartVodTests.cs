using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.Media;
using Xunit;

namespace SilentStream.Tests;

public sealed class MultipartVodTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-multipart-").FullName;
    private static readonly DateOnly Day = new(2026, 10, 26);
    private static readonly DateTime Start = Day.ToDateTime(new TimeOnly(9, 0));
    private readonly Runner _runner = new();
    private readonly Queue _queue = new();
    private readonly Assets _assets = new();
    private static PeriodBoundary Boundary => new(Day, 1, Start, Start.AddHours(1), "period-1", "1교시", "period", 1);
    private string File1 => Path.Combine(_dir, "first.mp4");
    private string File2 => Path.Combine(_dir, "second.mp4");
    private Sessions History => new([
        new(File1, Start.AddMinutes(-5), Start.AddMinutes(20)),
        new(File2, Start.AddMinutes(35), Start.AddMinutes(55))]);
    private VodSegmentService Cutter => new(History, _runner, new LogService(), Path.Combine(_dir, "vod"));
    private void MakeFiles(bool first = true)
    {
        if (first) File.WriteAllBytes(File1, new byte[16]);
        File.WriteAllBytes(File2, new byte[16]);
    }
    private VodCoordinator Coordinator()
    {
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var value = AppConfig.CreateDefault(); value.DeviceName = "m112"; config.Save(value);
        return new(new Scheduler(), Cutter, _queue, config, new LogService(), new Audio(), _assets,
            () => Guid.NewGuid().ToString("N"), History, new Schedule(), () => Start.AddHours(2));
    }

    [Fact]
    public async Task Every_recorded_overlap_becomes_a_separate_cut_even_with_a_long_gap()
    {
        MakeFiles();
        var clips = await Cutter.ExtractPeriodPartsAsync(Boundary, new HashSet<string>(), default);
        Assert.Equal(2, clips.Count);
        Assert.Equal([1, 2], clips.Select(c => c.PartNumber));
        Assert.All(clips, c => Assert.Equal(2, c.PartCount));
        Assert.Contains("-ss 300", _runner.Calls[0]);
        Assert.Contains("-t 1200", _runner.Calls[0]);
        Assert.Contains("-ss 0", _runner.Calls[1]);
        Assert.Contains("-t 1200", _runner.Calls[1]);
        Assert.DoesNotContain(_runner.Calls, c => c.Contains("concat"));
        Assert.NotEqual(clips[0].SourceKey, clips[1].SourceKey);
    }

    [Fact]
    public async Task Historical_files_are_recovered_without_an_active_recording_and_not_queued_twice()
    {
        MakeFiles();
        await Coordinator().RecoverCompletedPeriodsAsync(default);
        await Coordinator().RecoverCompletedPeriodsAsync(default);
        Assert.Equal(2, _queue.Jobs.Count); Assert.Equal(2, _runner.Calls.Count);
        Assert.All(_queue.Jobs, j => { Assert.Equal(Day, j.Date); Assert.Equal("period-1", j.SlotId); Assert.NotNull(j.SourceKey); });
        Assert.Contains(_queue.Jobs, j => j.Title.EndsWith("1교시 (1/2)"));
        Assert.Contains(_queue.Jobs, j => j.Title.EndsWith("1교시 (2/2)"));
        Assert.Equal(2, _assets.Items.Count);
    }

    [Fact]
    public async Task Missing_part_does_not_block_an_available_part_and_is_recovered_when_it_returns()
    {
        MakeFiles(first: false);
        await Coordinator().RecoverCompletedPeriodsAsync(default);
        Assert.EndsWith("1교시 (2/2)", Assert.Single(_queue.Jobs).Title);
        File.WriteAllBytes(File1, new byte[16]);
        await Coordinator().RecoverCompletedPeriodsAsync(default);
        Assert.Equal(2, _queue.Jobs.Count);
        Assert.Equal(2, _runner.Calls.Count); // the already queued second part was not cut again
    }

    [Fact]
    public async Task Ambiguous_legacy_upload_is_preserved_instead_of_duplicating_old_content()
    {
        MakeFiles();
        _queue.Jobs.Add(new UploadJob("legacy", "old.mp4", "old", Day, 1, "completed", 0, "old-video", "period-1"));
        await Coordinator().RecoverCompletedPeriodsAsync(default);
        Assert.Single(_queue.Jobs); Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task A_single_historical_part_still_uploads_without_a_part_suffix()
    {
        File.WriteAllBytes(File2, new byte[16]);
        var sessions = new Sessions([new(File2, Start, Start.AddHours(1))]);
        var cutter = new VodSegmentService(sessions, _runner, new LogService(), Path.Combine(_dir, "vod"));
        var clip = Assert.Single(await cutter.ExtractPeriodPartsAsync(Boundary, new HashSet<string>(), default));
        Assert.Equal(1, clip.PartCount); Assert.Equal(1, clip.PartNumber);
    }

    public void Dispose() => Directory.Delete(_dir, true);
    private sealed class Sessions(IReadOnlyList<RecordingSegment> parts) : IRecordingSessionInfo
    {
        public RecordingSession? Current => null;
        public IReadOnlyList<RecordingSegment> GetSegments(DateTime start, DateTime end) => parts.Where(p => p.StartLocal < end && p.EndLocal > start).ToArray();
    }
    private sealed class Runner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<(int ExitCode, string Output)> RunAsync(string file, string args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(args);var matches = Regex.Matches(args, "\"([^\"]+)\"");
            File.WriteAllBytes(matches[^1].Groups[1].Value, new byte[16]);return Task.FromResult((0, "ok"));
        }
    }
    private sealed class Queue : IUploadQueue
    {
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) => Jobs.Add(job);
        public IReadOnlyList<UploadJob> Snapshot() => Jobs.ToArray();
        public void Start(CancellationToken ct) { }
    }
    private sealed class Scheduler : IPeriodScheduler
    {
        public event EventHandler<PeriodBoundary>? PeriodStarted { add { } remove { } }
        public event EventHandler<PeriodBoundary>? PeriodEnded { add { } remove { } }
        public void Start(CancellationToken ct) { }
    }
    private sealed class Schedule : IPeriodScheduleStore
    {
        public DaySchedule ResolveForDate(DateOnly date) => date == Day
            ? new DaySchedule([new SchoolPeriod(1, new(9, 0), new(10, 0))]) : DaySchedule.Empty;
    }
    private sealed class Audio : ILocalAudioExportService
    {
        public Task<string?> ExportAsync(string file, string id, CancellationToken ct) => Task.FromResult<string?>(null);
    }
    private sealed class Assets : IPeriodAssetCatalog
    {
        public Dictionary<string, PeriodAsset> Items { get; } = [];
        public PeriodAsset Upsert(PeriodAsset asset) { Items[asset.Id] = asset; return asset; }
        public IReadOnlyList<PeriodAsset> Snapshot() => Items.Values.ToArray();
        public PeriodAsset? Find(string id) => Items.GetValueOrDefault(id);
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkAudioPath(string id, string path) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }
}
