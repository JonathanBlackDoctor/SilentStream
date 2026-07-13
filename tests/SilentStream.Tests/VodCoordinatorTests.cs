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

    public VodCoordinatorTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault());
    }

    private VodCoordinator Create() =>
        new(_scheduler, _vod, _queue, _configStore, new LogService(), () => "id-1");

    private static PeriodBoundary Boundary(int n) =>
        new(new DateOnly(2026, 6, 14), n,
            new DateTime(2026, 6, 14, 9, 0, 0), new DateTime(2026, 6, 14, 9, 50, 0));

    [Fact]
    public async Task Start_starts_the_queue_and_scheduler()
    {
        Create().Start(CancellationToken.None);
        Assert.True(_queue.Started);
        Assert.True(_scheduler.Started);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Period_ended_cuts_then_enqueues_with_the_formatted_title()
    {
        _vod.ResultPath = Path.Combine(_dir, "1교시.mp4");
        Create().Start(CancellationToken.None);

        _scheduler.RaiseEnded(Boundary(1));
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1, TimeSpan.FromSeconds(5));

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("1교시 - 2026-06-14", job.Title);     // D6 title format
        Assert.Equal(1, job.PeriodNumber);
        Assert.Equal(_vod.ResultPath, job.FilePath);
        Assert.Equal(UploadJobStatus.Pending, job.Status);
    }

    [Fact]
    public async Task Empty_cut_does_not_enqueue()
    {
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
        public Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct) =>
            Task.FromResult(ResultPath);
        public Task<string?> ExtractRangeAsync(
            RecordingSession session, DateTime startLocal, DateTime endLocal, string fileBaseLabel,
            CancellationToken ct) =>
            Task.FromResult(ResultPath);
    }

    private sealed class FakeQueue : IUploadQueue
    {
        public bool Started;
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) { lock (Jobs) { Jobs.Add(job); } }
        public IReadOnlyList<UploadJob> Snapshot() { lock (Jobs) { return Jobs.ToList(); } }
        public void Start(CancellationToken ct) => Started = true;
    }
}
