using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public sealed class SharedUploadQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-shared-budget-").FullName;
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTime _now = new(2026, 11, 1, 12, 0, 0);
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UploadQueue Queue(Uploader uploader, Budget budget) =>
        new(uploader, new ConfigStore(Path.Combine(_dir, "config.json")), new LogService(),
            Path.Combine(_dir, "queue.json"), () => _now, Park, int.MaxValue, 5, budget: budget);
    private Task Park(TimeSpan delay, CancellationToken ct)
    {
        _waiting.TrySetResult();
        return Task.Delay(Timeout.Infinite, ct);
    }
    private UploadJob Job(string id, int daysAgo = 0)
    {
        var file = Path.Combine(_dir, id + ".mp4");
        File.WriteAllBytes(file, [1, 2, 3]);
        return new(id, file, id, DateOnly.FromDateTime(_now.AddDays(-daysAgo)), 1, UploadJobStatus.Pending, 0, null, SourceKey: id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenialOrOfflineLeavesPendingFilesAndAttemptsUntouched(bool offline)
    {
        var budget = new Budget { Deny = !offline, Offline = offline };
        var uploader = new Uploader();
        var queue = Queue(uploader, budget);
        var job = Job("today");
        queue.Enqueue(job);
        queue.Start(_stop.Token);
        await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var saved = Assert.Single(queue.Snapshot());
        Assert.Equal(0, uploader.Calls);
        Assert.Equal(0, saved.Attempts);
        Assert.Equal(UploadJobStatus.Pending, saved.Status);
        Assert.Equal("today", saved.SourceKey);
        Assert.True(File.Exists(job.FilePath));
        Assert.Equal(saved, Assert.Single(Queue(new Uploader(), budget).Snapshot()));
    }

    [Fact]
    public async Task HangingBudgetDoesNotBlockStartOrQueueAndCancellationDoesNotMarkAnUploadUnknown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new Budget { OnReserve = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var uploader = new Uploader();
        var queue = Queue(uploader, budget);
        queue.Enqueue(Job("one"));
        queue.Start(_stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.Enqueue(Job("two"));
        _stop.Cancel();
        Assert.Equal(2, queue.Snapshot().Count);
        Assert.All(queue.Snapshot(), j => { Assert.Equal(UploadJobStatus.Pending, j.Status); Assert.False(j.NeedsUploadReconciliation); });
        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public async Task TodayPrecedesBacklogAndEachActualRetryRequestsItsOwnReservation()
    {
        var budget = new Budget();
        var uploader = new Uploader { FailFirst = true };
        // Retry delay advances immediately; idle wait still blocks on a signal.
        var queue = new UploadQueue(uploader, new ConfigStore(Path.Combine(_dir, "config.json")), new LogService(),
            Path.Combine(_dir, "queue.json"), () => _now, (_, _) => Task.CompletedTask, int.MaxValue, 5, budget: budget);
        queue.Enqueue(Job("old", 2));
        queue.Enqueue(Job("today"));
        queue.Start(_stop.Token);
        await Until(() => queue.Snapshot().All(j => j.Status == UploadJobStatus.Completed));
        Assert.Equal(new[] { "today", "today", "old" }, uploader.Titles);
        Assert.Equal(3, budget.Attempts.Count);
        Assert.Equal(3, budget.Attempts.Distinct().Count());
        Assert.Equal("video-2", queue.Snapshot().Single(j => j.Id == "today").VideoId);
        var restartedUploader = new Uploader();
        var restarted = Queue(restartedUploader, new Budget());
        restarted.Start(_stop.Token);
        Assert.Equal(0, restartedUploader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaPauseIsDurableAndSharedBeforeAnotherUpload(bool channel)
    {
        var budget = new Budget { ReportOffline = true };
        var uploader = new Uploader { Failure = channel
            ? new QuotaExceededException("channel") { RetryAfter = TimeSpan.FromHours(24) }
            : new QuotaExceededException("api") };
        var queue = Queue(uploader, budget);
        var job = Job("one");
        queue.Enqueue(job);
        queue.Start(_stop.Token);
        await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(budget.Pauses);
        Assert.Equal(channel ? "channel" : "api", budget.Pauses[0].Kind);
        Assert.Equal(UploadJobStatus.Pending, Assert.Single(queue.Snapshot()).Status);
        Assert.True(File.Exists(job.FilePath));
        var saved = File.ReadAllText(Path.Combine(_dir, "queue.json"));
        Assert.Contains("\"sharedPauseUntilUtc\":", saved);
        var delivered = new Budget();
        var nextUploader = new Uploader();
        Queue(nextUploader, delivered).Start(_stop.Token);
        await Until(() => delivered.Pauses.Count > 0);
        Assert.Equal(0, nextUploader.Calls);
        Assert.Empty(delivered.Attempts);
        Assert.Equal(budget.Pauses[0], delivered.Pauses[0]);
    }

    [Fact]
    public async Task AmbiguousUploadOutcomeStillRequiresReconciliationAndIsNeverAutoRetried()
    {
        var budget = new Budget();
        var uploader = new Uploader { Failure = new OperationCanceledException("lost acknowledgement") };
        var queue = Queue(uploader, budget);
        var job = Job("one");
        queue.Enqueue(job);
        queue.Start(_stop.Token);
        await Until(() => queue.Snapshot()[0].Status == UploadJobStatus.Failed);
        Assert.True(queue.Snapshot()[0].NeedsUploadReconciliation);
        Assert.Single(budget.Attempts);
        var nextUploader = new Uploader();
        Queue(nextUploader, new Budget()).Start(_stop.Token);
        Assert.Equal(0, nextUploader.Calls);
        Assert.True(File.Exists(job.FilePath));
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        Directory.Delete(_dir, true);
    }

    private sealed class Budget : IUploadBudget
    {
        public bool Deny, Offline, ReportOffline;
        public Func<CancellationToken, Task>? OnReserve;
        public List<string> Attempts { get; } = [];
        public List<(string Kind, DateTimeOffset Until)> Pauses { get; } = [];
        public async Task<UploadBudgetDecision> ReserveAsync(DateOnly date, string id, CancellationToken ct)
        {
            Attempts.Add(id);
            if (OnReserve is not null) await OnReserve(ct);
            if (Offline) throw new HttpRequestException("offline");
            return new(!Deny, DateTimeOffset.Now.AddDays(1), Deny ? "daily-budget" : "reserved");
        }
        public Task ReportPauseAsync(string kind, DateTimeOffset until, CancellationToken ct)
        {
            Pauses.Add((kind, until));
            return ReportOffline ? Task.FromException(new HttpRequestException("offline")) : Task.CompletedTask;
        }
    }

    private sealed class Uploader : IYouTubeUploadService
    {
        public int Calls;
        public bool FailFirst;
        public Exception? Failure;
        public List<string> Titles { get; } = [];
        public Task<string> UploadAsync(string path, string title, string privacy, CancellationToken ct)
        {
            Calls++;
            Titles.Add(title);
            if (Failure is not null) return Task.FromException<string>(Failure);
            if (FailFirst && Calls == 1) return Task.FromException<string>(new IOException("before insert"));
            return Task.FromResult("video-" + Calls);
        }
    }
}
