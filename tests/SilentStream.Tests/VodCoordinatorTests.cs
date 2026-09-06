using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class VodCoordinatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-coord-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeScheduler _scheduler = new();
    private readonly FakeVod _vod = new();
    private readonly FakeQueue _queue = new();
    private readonly FakeAudioExport _audio = new();
    private readonly FakeAssetCatalog _assets = new();

    public VodCoordinatorTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.DeviceName = "m111";
        _configStore.Save(config);
    }

    private VodCoordinator Create() =>
        new(_scheduler, _vod, _queue, _configStore, new LogService(), _audio, _assets,
            () => "id-1");

    private static PeriodBoundary Boundary(int n) =>
        new(new DateOnly(2026, 6, 14), n,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0),
            $"period-{n}", $"{n}교시", ScheduleSlotKinds.Period, n);

    [Fact]
    public void Start_starts_queue_and_scheduler_without_legacy_approval_service()
    {
        Create().Start(CancellationToken.None);
        Assert.True(_queue.Started);
        Assert.True(_scheduler.Started);
    }

    [Fact]
    public async Task Period_end_is_always_processed_automatically()
    {
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Single(_queue.Jobs);
        Assert.True(_vod.PeriodCutCalled);
    }

    [Fact]
    public async Task Period_ended_cuts_then_enqueues_with_the_formatted_title()
    {
        _vod.ResultPath = Path.Combine(_dir, "1교시.mp4");
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1, TimeSpan.FromSeconds(5));

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("[영상] m111 | 2026-06-14 | 1교시", job.Title);
        Assert.Equal(1, job.PeriodNumber);
        Assert.Equal(_vod.ResultPath, job.FilePath);
        Assert.Equal(UploadJobStatus.Pending, job.Status);
        Assert.Equal("period-1", job.ResolvedSlotId);
        Assert.Equal("1교시", job.ResolvedSlotLabel);
        var asset = Assert.Single(_assets.Assets);
        Assert.Equal("id-1", asset.Id);
        Assert.Equal(_audio.ResultPath, asset.AudioPath);
    }

    [Fact]
    public async Task Empty_cut_does_not_enqueue()
    {
        _vod.ResultPath = null;
        Create().Start(CancellationToken.None);
        _scheduler.RaiseEnded(Boundary(1));
        await Task.Delay(150);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public async Task Lunch_is_cut_and_enqueued_as_an_independent_slot()
    {
        _vod.ResultPath = Path.Combine(_dir, "점심시간.mp4");
        Create().Start(CancellationToken.None);
        var date = new DateOnly(2026, 6, 15);
        _scheduler.RaiseEnded(new PeriodBoundary(
            date, 0, date.ToDateTime(new TimeOnly(12, 25)), date.ToDateTime(new TimeOnly(13, 25)),
            "lunch", "점심시간", ScheduleSlotKinds.Lunch, 5));

        await WaitUntilAsync(() => _queue.Jobs.Count >= 1, TimeSpan.FromSeconds(5));

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("lunch", job.ResolvedSlotId);
        Assert.Equal("점심시간", job.ResolvedSlotLabel);
        Assert.Equal(ScheduleSlotKinds.Lunch, job.ResolvedSlotKind);
        Assert.Equal(5, job.ResolvedSlotOrder);
        Assert.Contains("점심시간", job.Title);
        Assert.Equal("lunch", Assert.Single(_assets.Assets).ResolvedSlotId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeScheduler : IPeriodScheduler
    {
        public bool Started;
        public event EventHandler<PeriodBoundary>? PeriodStarted;
        public event EventHandler<PeriodBoundary>? PeriodEnded;
        public void Start(CancellationToken ct) => Started = true;
        public void RaiseEnded(PeriodBoundary b) => PeriodEnded?.Invoke(this, b);
        public void RaiseStarted(PeriodBoundary b) => PeriodStarted?.Invoke(this, b);
    }

    private sealed class FakeVod : IVodSegmentService
    {
        public string? ResultPath = "/vod/x.mp4";
        public bool PeriodCutCalled;
        public Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct)
        {
            PeriodCutCalled = true;
            return Task.FromResult(ResultPath);
        }
        public Task<string?> ExtractRangeAsync(
            RecordingSession session, DateTime startLocal, DateTime endLocal, string fileBaseLabel,
            CancellationToken ct) => Task.FromResult(ResultPath);
    }

    private sealed class FakeQueue : IUploadQueue
    {
        public bool Started;
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) { lock (Jobs) { Jobs.Add(job); } }
        public IReadOnlyList<UploadJob> Snapshot() { lock (Jobs) { return Jobs.ToList(); } }
        public void Start(CancellationToken ct) => Started = true;
    }

    private sealed class FakeAudioExport : ILocalAudioExportService
    {
        public string? ResultPath = "/assets/audio.m4a";
        public Task<string?> ExportAsync(string sourceVideoPath, string assetId, CancellationToken ct) =>
            Task.FromResult(ResultPath);
    }

    private sealed class FakeAssetCatalog : IPeriodAssetCatalog
    {
        public List<PeriodAsset> Assets { get; } = [];
        public PeriodAsset Upsert(PeriodAsset asset) { Assets.Add(asset); return asset; }
        public IReadOnlyList<PeriodAsset> Snapshot() => Assets.ToList();
        public PeriodAsset? Find(string id) => Assets.SingleOrDefault(a => a.Id == id);
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkAudioPath(string id, string audioPath) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }
}
