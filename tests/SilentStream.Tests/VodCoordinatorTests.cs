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
    private readonly FakeSplits _splits = new();
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
        new(_scheduler, _vod, _queue, _configStore, _splits, new LogService(), _audio, _assets,
            () => "id-1");

    /// <summary>The legacy immediate-cut tests below opt out of v8 approval mode.</summary>
    private void UseLegacyImmediateCut() =>
        _configStore.Update(c => c.Periods.RequireApproval = false);

    private static PeriodBoundary Boundary(int n) =>
        new(new DateOnly(2026, 6, 14), n,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

    [Fact]
    public async Task Start_starts_the_queue_scheduler_and_split_service()
    {
        Create().Start(CancellationToken.None);
        Assert.True(_queue.Started);
        Assert.True(_scheduler.Started);
        Assert.True(_splits.Started);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Approval_mode_routes_period_end_to_the_split_service()
    {
        // v8 default (RequireApproval=true): no immediate cut/upload — the boundary becomes a
        // pending split the operator handles from the phone.
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await WaitUntilAsync(() => _splits.Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal(1, Assert.Single(_splits.Boundaries).PeriodNumber);
        Assert.Empty(_queue.Jobs);
        Assert.False(_vod.PeriodCutCalled);
    }

    [Fact]
    public async Task Period_ended_cuts_then_enqueues_with_the_formatted_title()
    {
        UseLegacyImmediateCut();
        _vod.ResultPath = Path.Combine(_dir, "1교시.mp4");
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1, TimeSpan.FromSeconds(5));

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("[영상] m111 | 2026-06-14 | 1교시", job.Title);
        Assert.Equal(1, job.PeriodNumber);
        Assert.Equal(_vod.ResultPath, job.FilePath);
        Assert.Equal(UploadJobStatus.Pending, job.Status);
        var asset = Assert.Single(_assets.Assets);
        Assert.Equal("id-1", asset.Id);
        Assert.Equal(_audio.ResultPath, asset.AudioPath);
    }

    [Fact]
    public async Task Empty_cut_does_not_enqueue()
    {
        UseLegacyImmediateCut();
        _vod.ResultPath = null; // no extractable segment
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await Task.Delay(150);

        Assert.Empty(_queue.Jobs);
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
            CancellationToken ct) =>
            Task.FromResult(ResultPath);
    }

    private sealed class FakeSplits : ISplitApprovalService
    {
        public bool Started;
        public List<PeriodBoundary> Boundaries { get; } = [];
        public int Count { get { lock (Boundaries) { return Boundaries.Count; } } }
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<PendingSplit> Snapshot() => [];
        public MergeChain? OpenChain() => null;
        public void Start(CancellationToken ct) => Started = true;
        public void OnPeriodEnded(PeriodBoundary boundary)
        {
            lock (Boundaries) { Boundaries.Add(boundary); }
        }
        public SplitActionOutcome Approve(string id, DateTime? startLocal, DateTime? endLocal) =>
            new(true, null);
        public SplitActionOutcome Merge(string id) => new(true, null);
        public SplitActionOutcome CancelMerge() => new(true, null);
        public SplitActionOutcome Skip(string id) => new(true, null);
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
