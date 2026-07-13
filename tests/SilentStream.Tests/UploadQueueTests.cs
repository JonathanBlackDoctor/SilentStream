using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public class UploadQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-queue-").FullName;
    private readonly string _queueFile;
    private readonly ConfigStore _configStore;
    private readonly VirtualClock _clock = new(new DateTime(2026, 6, 15, 10, 0, 0));

    public UploadQueueTests()
    {
        _queueFile = Path.Combine(_dir, "upload_queue.json");
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault());
    }

    private UploadJob MakeJob(int period)
    {
        var path = Path.Combine(_dir, $"{period}교시.mp4");
        File.WriteAllBytes(path, new byte[256]);
        return new UploadJob($"job-{period}", path, $"{period}교시 - 2026-06-15",
            new DateOnly(2026, 6, 15), period, UploadJobStatus.Pending, 0, null);
    }

    private UploadQueue Create(IYouTubeUploadService uploader, int maxPerDay = 6) =>
        CreateWith(uploader, _clock, maxPerDay);

    private UploadQueue CreateWith(IYouTubeUploadService uploader, VirtualClock clock, int maxPerDay = 6) =>
        new(uploader, _configStore, new LogService(), _queueFile, () => clock.Now, clock.Delay,
            maxPerDay, maxAttempts: 5);

    [Fact]
    public async Task Uploads_all_jobs_marks_completed_and_deletes_cut_files()
    {
        var uploader = new FakeUploader();
        var queue = Create(uploader);
        var job1 = MakeJob(1);
        var job2 = MakeJob(2);
        queue.Enqueue(job1);
        queue.Enqueue(job2);

        using var cts = new CancellationTokenSource();
        queue.Start(cts.Token);

        await WaitUntilAsync(() => queue.Snapshot().All(j => j.Status == UploadJobStatus.Completed),
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.All(queue.Snapshot(), j => Assert.Equal(UploadJobStatus.Completed, j.Status));
        Assert.All(queue.Snapshot(), j => Assert.NotNull(j.VideoId));
        Assert.False(File.Exists(job1.FilePath)); // cut files removed after upload
        Assert.False(File.Exists(job2.FilePath));
        Assert.Equal(2, uploader.Calls);
    }

    [Fact]
    public async Task Completed_upload_marks_its_durable_asset_with_the_youtube_id()
    {
        var uploader = new FakeUploader();
        var catalog = new FakeAssetCatalog();
        var job = MakeJob(1);
        catalog.Upsert(new PeriodAsset(job.Id, job.Date, job.PeriodNumber, job.Title));
        var queue = new UploadQueue(uploader, _configStore, new LogService(), _queueFile,
            () => _clock.Now, _clock.Delay, maxPerDay: 6, maxAttempts: 5, assetCatalog: catalog);
        queue.Enqueue(job);

        using var cts = new CancellationTokenSource();
        queue.Start(cts.Token);
        await WaitUntilAsync(() => queue.Snapshot()[0].Status == UploadJobStatus.Completed,
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.Equal("vid-1", Assert.Single(catalog.Assets).VideoId);
    }

    [Fact]
    public async Task Quota_exceeded_keeps_job_pending_then_resumes_after_reset()
    {
        // First attempt hits quota; after the (virtual) PT reset the retry succeeds.
        var uploader = new FakeUploader { ThrowQuotaOnFirstCall = true };
        var queue = Create(uploader);
        queue.Enqueue(MakeJob(7)); // the "7교시" overflow case (D7)

        using var cts = new CancellationTokenSource();
        queue.Start(cts.Token);

        await WaitUntilAsync(() => queue.Snapshot()[0].Status == UploadJobStatus.Completed,
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.Equal(UploadJobStatus.Completed, queue.Snapshot()[0].Status);
        Assert.True(uploader.Calls >= 2); // failed once (quota), then succeeded after reset
    }

    [Fact]
    public async Task Pending_job_survives_a_restart_and_uploads_on_the_next_run()
    {
        // Run 1: uploader always hits quota. Parking clock so the quota wait blocks (in production
        // the real Task.Delay waits hours) → the worker attempts once, leaves the job pending, parks.
        var run1Clock = new VirtualClock(new DateTime(2026, 6, 15, 10, 0, 0), parkLargeWaits: true);
        var quotaUploader = new FakeUploader { AlwaysQuota = true };
        var run1 = CreateWith(quotaUploader, run1Clock);
        run1.Enqueue(MakeJob(1));
        using (var cts1 = new CancellationTokenSource())
        {
            run1.Start(cts1.Token);
            await WaitUntilAsync(() => quotaUploader.Calls >= 1, TimeSpan.FromSeconds(5));
            cts1.Cancel();
        }
        Assert.Equal(UploadJobStatus.Pending, run1.Snapshot()[0].Status);

        // Run 2: brand-new queue from the same file (simulating an app restart). Advancing clock so
        // it passes the next PT reset and uploads the persisted job.
        var run2Clock = new VirtualClock(new DateTime(2026, 6, 15, 10, 0, 0));
        var okUploader = new FakeUploader();
        var run2 = CreateWith(okUploader, run2Clock);
        Assert.Single(run2.Snapshot()); // job persisted across the restart
        using var cts2 = new CancellationTokenSource();
        run2.Start(cts2.Token);

        await WaitUntilAsync(() => run2.Snapshot()[0].Status == UploadJobStatus.Completed,
            TimeSpan.FromSeconds(5));
        cts2.Cancel();

        Assert.Equal(UploadJobStatus.Completed, run2.Snapshot()[0].Status);
    }

    [Fact]
    public async Task Daily_cap_defers_overflow_across_a_reset_until_all_complete()
    {
        // 3 jobs, cap of 2/day: 2 upload today, the 3rd waits for the next reset (D7).
        var uploader = new FakeUploader();
        var queue = Create(uploader, maxPerDay: 2);
        queue.Enqueue(MakeJob(1));
        queue.Enqueue(MakeJob(2));
        queue.Enqueue(MakeJob(3));

        using var cts = new CancellationTokenSource();
        queue.Start(cts.Token);

        await WaitUntilAsync(() => queue.Snapshot().All(j => j.Status == UploadJobStatus.Completed),
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.Equal(3, queue.Snapshot().Count(j => j.Status == UploadJobStatus.Completed));
        Assert.True(_clock.QuotaPauses >= 1); // the cap forced at least one wait-for-reset
    }

    [Fact]
    public async Task Prune_caps_completed_jobs_and_never_drops_a_pending_one()
    {
        // Cap = 1 completed; 3 jobs with a daily cap of 2 so the 3rd stays pending while the queue
        // parks at the quota reset. Prune must trim completed to 1 yet keep the pending 교시.
        var clock = new VirtualClock(new DateTime(2026, 6, 15, 10, 0, 0), parkLargeWaits: true);
        var uploader = new FakeUploader();
        var queue = new UploadQueue(uploader, _configStore, new LogService(), _queueFile,
            () => clock.Now, clock.Delay, maxPerDay: 2, maxAttempts: 5,
            maxCompletedRetained: 1, maxFailedRetained: 0);
        queue.Enqueue(MakeJob(1));
        queue.Enqueue(MakeJob(2));
        queue.Enqueue(MakeJob(3));

        using var cts = new CancellationTokenSource();
        queue.Start(cts.Token);

        await WaitUntilAsync(() =>
                queue.Snapshot().Count(j => j.Status == UploadJobStatus.Completed) == 1 &&
                queue.Snapshot().Any(j => j.Status == UploadJobStatus.Pending),
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        var snap = queue.Snapshot();
        Assert.Equal(1, snap.Count(j => j.Status == UploadJobStatus.Completed)); // capped to 1
        Assert.Single(snap, j => j.Status == UploadJobStatus.Pending);           // pending preserved
        Assert.DoesNotContain(snap, j => j.Id == "job-1");                       // oldest completed pruned
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

    /// <summary>
    /// Virtual clock whose Delay normally advances time instantly (so quota waits don't block tests).
    /// When <paramref name="parkLargeWaits"/> is set, a long wait-until-reset blocks instead — used to
    /// model an always-failing uploader where advancing past the reset would just spin.
    /// </summary>
    private sealed class VirtualClock(DateTime start, bool parkLargeWaits = false)
    {
        private readonly object _lock = new();
        private DateTime _now = start;
        public int QuotaPauses { get; private set; }

        public DateTime Now { get { lock (_lock) { return _now; } } }

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled(ct);
            }
            var isResetWait = span > TimeSpan.FromHours(1);
            if (isResetWait)
            {
                lock (_lock) { QuotaPauses++; }
                if (parkLargeWaits)
                {
                    return Task.Delay(System.Threading.Timeout.Infinite, ct);
                }
            }
            lock (_lock) { _now = _now.Add(span); }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUploader : IYouTubeUploadService
    {
        public int Calls;
        public bool ThrowQuotaOnFirstCall;
        public bool AlwaysQuota;

        public Task<string> UploadAsync(string filePath, string title, string privacy, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls);
            if (AlwaysQuota || (ThrowQuotaOnFirstCall && call == 1))
            {
                throw new QuotaExceededException("simulated quota");
            }
            return Task.FromResult($"vid-{call}");
        }
    }

    private sealed class FakeAssetCatalog : IPeriodAssetCatalog
    {
        public List<PeriodAsset> Assets { get; } = [];
        public PeriodAsset Upsert(PeriodAsset asset) { Assets.Add(asset); return asset; }
        public IReadOnlyList<PeriodAsset> Snapshot() => Assets.ToList();
        public PeriodAsset? Find(string id) => Assets.SingleOrDefault(a => a.Id == id);
        public bool MarkUploaded(string id, string videoId)
        {
            var index = Assets.FindIndex(a => a.Id == id);
            if (index < 0) return false;
            Assets[index] = Assets[index] with { VideoId = videoId };
            return true;
        }
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }
}
