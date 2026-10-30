using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class UploadQueueDurabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-durable-").FullName;
    private string QueueFile => Path.Combine(_dir, "queue.json");
    private readonly ConfigStore _config;

    public UploadQueueDurabilityTests()
    {
        _config = new ConfigStore(Path.Combine(_dir, "config.json"));
        _config.Save(AppConfig.CreateDefault());
    }

    private UploadJob Job(string id = "one")
    {
        var path = Path.Combine(_dir, id + ".mp4");
        File.WriteAllBytes(path, new byte[4096]);
        return new UploadJob(id, path, id, new DateOnly(2026, 10, 28), 1,
            UploadJobStatus.Pending, 0, null, SourceKey: "source-" + id);
    }

    private UploadQueue Queue(Uploader uploader, IYouTubePlaylistService? playlists = null) =>
        new(uploader, _config, new LogService(), QueueFile, () => DateTime.Now,
            (span, ct) => Task.Delay(span > TimeSpan.FromHours(1) ? span : TimeSpan.FromMilliseconds(50), ct),
            maxPerDay: 1, maxAttempts: 5, playlistService: playlists);

    [Fact]
    public void Playlist_cancellation_cannot_requeue_an_acknowledged_upload()
    {
        var uploader = new Uploader();
        var queue = Queue(uploader, new CanceledPlaylist());
        queue.Enqueue(Job());
        using var stop = new CancellationTokenSource();
        queue.Start(stop.Token);
        stop.Cancel();
        var job = Assert.Single(queue.Snapshot());
        Assert.Equal(UploadJobStatus.Completed, job.Status);
        Assert.Equal("video-one", job.VideoId);
        Assert.Equal(1, uploader.Calls);
        Assert.Equal(UploadJobStatus.Completed, Assert.Single(Queue(new Uploader()).Snapshot()).Status);
    }

    [Fact]
    public async Task Post_upload_disk_failure_preserves_success_and_retries_only_the_save()
    {
        var uploader = new Uploader { BeforeReturn = () => Directory.CreateDirectory(QueueFile + ".tmp") };
        var queue = Queue(uploader);
        var original = Job();
        queue.Enqueue(original);
        using var stop = new CancellationTokenSource();
        queue.Start(stop.Token);
        try
        {
            Assert.Equal(UploadJobStatus.Completed, Assert.Single(queue.Snapshot()).Status);
            Assert.True(File.Exists(original.FilePath));
            Directory.Delete(QueueFile + ".tmp");
            for (var n = 0; n < 100; n++)
            {
                if (Assert.Single(Queue(new Uploader()).Snapshot()).Status == UploadJobStatus.Completed) break;
                await Task.Delay(20);
            }
            Assert.Equal(UploadJobStatus.Completed, Assert.Single(Queue(new Uploader()).Snapshot()).Status);
            Assert.Equal(1, uploader.Calls);
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public void Unreadable_queue_is_not_silently_replaced_with_an_empty_queue()
    {
        File.WriteAllText(QueueFile, "{broken");
        Assert.ThrowsAny<Exception>(() => Queue(new Uploader()).Snapshot());
        Assert.Equal("{broken", File.ReadAllText(QueueFile));
    }

    [Fact]
    public void Duplicate_identity_is_not_added_twice()
    {
        var queue = Queue(new Uploader());
        var job = Job();
        queue.Enqueue(job);
        queue.Enqueue(job with { Id = "another-id" });
        Assert.Single(queue.Snapshot());
    }

    [Fact]
    public void Interrupted_inflight_upload_is_held_for_reconciliation_after_restart()
    {
        var uploader = new Uploader();
        var queue = Queue(uploader);
        queue.Enqueue(Job() with { Status = UploadJobStatus.Uploading });
        using var stop = new CancellationTokenSource();
        queue.Start(stop.Token);
        stop.Cancel();
        var job = Assert.Single(queue.Snapshot());
        Assert.Equal(UploadJobStatus.Failed, job.Status);
        Assert.True(job.NeedsUploadReconciliation);
        Assert.Equal(0, uploader.Calls);
        Assert.Equal(0, queue.RetryFailed([job.Id]));
    }

    [Fact]
    public void Pending_job_with_durable_video_id_never_uploads_again()
    {
        var uploader = new Uploader();
        var queue = Queue(uploader);
        queue.Enqueue(Job() with { VideoId = "already-uploaded" });
        using var stop = new CancellationTokenSource();
        queue.Start(stop.Token);
        stop.Cancel();
        Assert.Equal(UploadJobStatus.Completed, Assert.Single(queue.Snapshot()).Status);
        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public void Queue_corruption_does_not_throw_out_of_application_startup()
    {
        File.WriteAllText(QueueFile, "{broken");
        var uploader = new Uploader();
        using var stop = new CancellationTokenSource();
        Queue(uploader).Start(stop.Token);
        stop.Cancel();
        Assert.Equal(0, uploader.Calls);
        Assert.Equal("{broken", File.ReadAllText(QueueFile));
    }

    [Fact]
    public void Queue_does_not_prune_the_only_surviving_upload_receipt_or_unresolved_failure()
    {
        var queue = new UploadQueue(new Uploader(), _config, new LogService(), QueueFile, () => DateTime.Now,
            Task.Delay, 10, 5, maxCompletedRetained: 0, maxFailedRetained: 0);
        queue.Enqueue(Job() with { Status = UploadJobStatus.Completed, VideoId = "keep-video-id" });
        queue.Enqueue(Job("failure") with { Status = UploadJobStatus.Failed, Attempts = 5 });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        queue.Start(stop.Token);
        Assert.Equal(2, queue.Snapshot().Count);
        Assert.Equal("keep-video-id", queue.Snapshot()[0].VideoId);
    }

    [Fact]
    public void Later_asset_metadata_cannot_erase_an_upload_receipt()
    {
        var catalog = new PeriodAssetCatalog(Path.Combine(_dir, "assets.json"));
        var asset = new PeriodAsset("one", new DateOnly(2026, 10, 28), 1, "one", SourceKey: "same-source");
        catalog.Upsert(asset);
        catalog.MarkUploaded("one", "keep-video-id", 4096, 1);
        catalog.Upsert(asset with { AudioPath = "audio.m4a" });
        var saved = new PeriodAssetCatalog(Path.Combine(_dir, "assets.json")).Find("one")!;
        Assert.Equal("keep-video-id", saved.VideoId);
        Assert.Equal(1, saved.UploadValidationVersion);
        Assert.Equal(4096, saved.UploadedFileSizeBytes);
    }

    private sealed class Uploader : IYouTubeUploadService
    {
        public int Calls;
        public Action? BeforeReturn;
        public Task<string> UploadAsync(string filePath, string title, string privacy, CancellationToken ct)
        {
            Calls++;
            BeforeReturn?.Invoke();
            return Task.FromResult("video-one");
        }
    }

    private sealed class CanceledPlaylist : IYouTubePlaylistService
    {
        public bool HasRetryableFailures => false;
        public IReadOnlyList<DailyPlaylistRecord> Snapshot(string room, DateOnly from, DateOnly to) => [];
        public Task<DailyPlaylistRecord> EnsurePlaylistAsync(string room, DateOnly date, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task AddVideoAsync(string room, DateOnly date, string videoId, CancellationToken ct) =>
            throw new OperationCanceledException("Playlist canceled after upload acknowledgement");
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
