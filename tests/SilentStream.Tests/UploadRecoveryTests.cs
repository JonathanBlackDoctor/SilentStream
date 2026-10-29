using System.Net;
using System.Text;
using Google;
using Google.Apis.Requests;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public sealed class UploadRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-retry-").FullName;

    [Theory]
    [InlineData(429, null, "Quota exceeded for quota metric 'Video Uploads' and limit 'Video Uploads per day'.", true)]
    [InlineData(429, "quotaExceeded", "quota", true)]
    [InlineData(403, "quotaExceeded", "quota", true)]
    [InlineData(403, "dailyLimitExceeded", "quota", true)]
    [InlineData(429, "rateLimitExceeded", "Too many requests per minute", false)]
    [InlineData(403, "rateLimitExceeded", "Too many requests", false)]
    [InlineData(403, "forbidden", "Permission denied", false)]
    [InlineData(500, "quotaExceeded", "quota", false)]
    public void Daily_limit_is_distinct_from_temporary_throttling(int status, string? reason, string message, bool daily)
    {
        var exception = new GoogleApiException("youtube", message)
        {
            HttpStatusCode = (HttpStatusCode)status,
            Error = new RequestError
            {
                Message = message,
                Errors = reason is null ? null : [new SingleError { Reason = reason }]
            }
        };
        Assert.Equal(daily, UploadErrorClassifier.IsDailyQuota(exception));
    }

    [Fact]
    public void Channel_upload_limit_is_separate_from_the_api_quota_reset()
    {
        var exception = new GoogleApiException("youtube", "The user has exceeded the number of videos they may upload.")
        {
            HttpStatusCode = HttpStatusCode.BadRequest,
            Error = new RequestError { Errors = [new SingleError { Reason = "uploadLimitExceeded" }] }
        };
        Assert.True(UploadErrorClassifier.IsChannelUploadLimit(exception));
        Assert.False(UploadErrorClassifier.IsDailyQuota(exception));
        exception.HttpStatusCode = HttpStatusCode.InternalServerError;
        Assert.False(UploadErrorClassifier.IsChannelUploadLimit(exception));
    }

    [Fact]
    public async Task Channel_limit_survives_a_restart_without_spending_the_job_retry_budget()
    {
        var config = new ConfigStore(Path.Combine(_dir, "channel-config.json"));
        config.Save(AppConfig.CreateDefault());
        var path = Path.Combine(_dir, "channel.mp4");
        File.WriteAllBytes(path, Mp4(1, true));
        var queueFile = Path.Combine(_dir, "channel-queue.json");
        var now = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
        var queue = new UploadQueue(new LimitedUploader(), config, new LogService(), queueFile,
            () => now, (_,ct) => Task.Delay(Timeout.Infinite,ct), int.MaxValue, 5);
        queue.Enqueue(new UploadJob("channel",path,"channel",new DateOnly(2026,10,28),1,"pending",0,null));
        using(var cancellation = new CancellationTokenSource())
        {
            queue.Start(cancellation.Token);
            for(var i=0;i<200 && queue.PausedUntilUtc is null;i++) await Task.Delay(10);
            cancellation.Cancel();
        }
        Assert.Equal(new DateTimeOffset(now).AddHours(24), queue.PausedUntilUtc);
        Assert.Equal("pending",Assert.Single(queue.Snapshot()).Status);
        Assert.Equal(0,Assert.Single(queue.Snapshot()).Attempts);
        var uploader = new Uploader();
        var restarted = new UploadQueue(uploader, config, new LogService(), queueFile,
            () => now, Task.Delay, int.MaxValue, 5);
        Assert.Equal(queue.PausedUntilUtc,restarted.PausedUntilUtc);
        now=now.AddHours(25);
        using var resumed = new CancellationTokenSource();
        restarted.Start(resumed.Token);
        for(var i=0;i<200 && uploader.Calls==0;i++) await Task.Delay(10);
        resumed.Cancel();
        Assert.Equal(1,uploader.Calls);
        Assert.Equal("completed",Assert.Single(restarted.Snapshot()).Status);
    }

    private sealed class LimitedUploader : IYouTubeUploadService
    {
        public Task<string> UploadAsync(string path,string title,string privacy,CancellationToken token) =>
            throw new QuotaExceededException("channel limit") { RetryAfter=TimeSpan.FromHours(24) };
    }

    [Fact]
    public void Mp4_requires_both_media_samples_and_data_and_rejects_truncated_boxes()
    {
        var valid = Path.Combine(_dir, "valid.mp4");
        File.WriteAllBytes(valid, Mp4(3, true));
        Assert.True(Mp4MediaSamples.HasSamples(valid));
        var empty = Path.Combine(_dir, "empty.mp4");
        File.WriteAllBytes(empty, Mp4(0, false));
        Assert.False(Mp4MediaSamples.HasSamples(empty));
        File.WriteAllBytes(empty, Mp4(3, false));
        Assert.False(Mp4MediaSamples.HasSamples(empty));
        File.WriteAllBytes(empty, [0, 0, 0, 64, 109, 111, 111, 118]);
        Assert.False(Mp4MediaSamples.HasSamples(empty));
        Assert.False(Mp4MediaSamples.HasSamples(Path.Combine(_dir, "missing.mp4")));
    }

    [Fact]
    public async Task Selected_failed_jobs_retry_once_without_reuploading_completed_or_empty_files()
    {
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        config.Save(AppConfig.CreateDefault());
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, Mp4(1, true));
        var empty = Path.Combine(_dir, "empty.mp4");
        File.WriteAllBytes(empty, Mp4(0, false));
        var uploader = new Uploader();
        var queue = new UploadQueue(uploader, config, new LogService(), Path.Combine(_dir, "queue.json"),
            () => DateTime.Now, Task.Delay, int.MaxValue, 5);
        var template = new UploadJob("selected", file, "clip", new DateOnly(2026, 10, 28), 1,
            UploadJobStatus.Failed, 5, null);
        queue.Enqueue(template);
        queue.Enqueue(template with { Id = "completed", Status = UploadJobStatus.Completed, VideoId = "existing" });
        queue.Enqueue(template with { Id = "unselected" });
        queue.Enqueue(template with { Id = "empty", FilePath = empty });
        queue.Enqueue(template with { Id = "missing", FilePath = Path.Combine(_dir, "missing.mp4") });
        Assert.Equal(1, queue.RetryFailed(["selected", "selected", "completed", "empty", "missing"]));
        Assert.Equal(0, queue.RetryFailed(["selected"]));
        Assert.Equal(UploadJobStatus.Failed, queue.Snapshot().Single(j => j.Id == "unselected").Status);
        Assert.Equal("existing", queue.Snapshot().Single(j => j.Id == "completed").VideoId);
        using var cancellation = new CancellationTokenSource();
        queue.Start(cancellation.Token);
        for (var i = 0; i < 200 && uploader.Calls == 0; i++) await Task.Delay(10);
        cancellation.Cancel();
        Assert.Equal(1, uploader.Calls);
        Assert.Equal(UploadJobStatus.Completed, queue.Snapshot().Single(j => j.Id == "selected").Status);
        Assert.Equal(0, queue.Snapshot().Single(j => j.Id == "selected").Attempts);
    }

    [Fact]
    public void Retry_does_not_clear_a_persisted_quota_pause()
    {
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        config.Save(AppConfig.CreateDefault());
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, Mp4(1, true));
        var now = new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc);
        var job = new UploadJob("failed", file, "clip", new DateOnly(2026, 10, 28), 1,
            UploadJobStatus.Failed, 5, null);
        var queueFile = Path.Combine(_dir, "quota.json");
        File.WriteAllText(queueFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            quotaPtDate = "2026-10-27", quotaCount = 10, jobs = new[] { job }
        }));
        var queue = new UploadQueue(new Uploader(), config, new LogService(), queueFile,
            () => now, Task.Delay, 10, 5);
        var pause = queue.PausedUntilUtc;
        Assert.NotNull(pause);
        Assert.Equal(1, queue.RetryFailed(["failed"]));
        Assert.Equal(pause, queue.PausedUntilUtc);
    }

    [Fact]
    public void Legacy_quota_migration_repairs_only_latest_class_with_valid_unuploaded_media_once()
    {
        var config = new ConfigStore(Path.Combine(_dir, "migration-config.json"));
        config.Save(AppConfig.CreateDefault());
        var valid = Path.Combine(_dir, "migration-valid.mp4");
        var empty = Path.Combine(_dir, "migration-empty.mp4");
        File.WriteAllBytes(valid, Mp4(1, true));
        File.WriteAllBytes(empty, Mp4(0, false));
        var now = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var pause = new DateTimeOffset(now).AddHours(23);
        var job = new UploadJob("recover", valid, "clip", new DateOnly(2026, 10, 28), 1, "failed", 5, null);
        var jobs = new[] { job, job with { Id = "older", Date = job.Date.AddDays(-1) },
            job with { Id = "today", Date = job.Date.AddDays(1) }, job with { Id = "empty", FilePath = empty },
            job with { Id = "missing", FilePath = Path.Combine(_dir, "missing.mp4") },
            job with { Id = "completed", Status = "completed", VideoId = "existing" },
            job with { Id = "receipt" }, job with { Id = "receipt-source", SourceKey = "already-uploaded" },
            job with { Id = "pending", Status = "pending" } };
        var file = Path.Combine(_dir, "migration-queue.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { uploadLimitUntilUtc = pause, jobs }));
        var assets = new PeriodAssetCatalog(Path.Combine(_dir, "migration-assets.json"));
        assets.Upsert(new PeriodAsset("receipt", job.Date, 1, "receipt", VideoId: "known"));
        assets.Upsert(new PeriodAsset("different-id", job.Date, 1, "receipt", VideoId: "known-source", SourceKey: "already-uploaded"));
        var uploader = new Uploader();
        UploadQueue Open() => new(uploader, config, new LogService(), file, () => now, Task.Delay,
            int.MaxValue, 5, assetCatalog: assets);
        var queue = Open();
        queue.Start(new CancellationToken(true));
        Assert.Equal(pause, queue.PausedUntilUtc);
        Assert.Equal(0, uploader.Calls);
        var repaired = queue.Snapshot().Single(j => j.Id == "recover");
        Assert.Equal("pending", repaired.Status);
        Assert.Equal(0, repaired.Attempts);
        foreach (var id in new[] { "older", "today", "empty", "missing", "receipt", "receipt-source" })
            Assert.Equal("failed", queue.Snapshot().Single(j => j.Id == id).Status);
        Assert.Equal("existing", queue.Snapshot().Single(j => j.Id == "completed").VideoId);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(1, saved["quotaRecoveryVersion"]!.GetValue<int>());
        saved["jobs"]!.AsArray().Single(j => j!["id"]!.GetValue<string>() == "recover")!["status"] = "failed";
        File.WriteAllText(file, saved.ToJsonString());
        var restarted = Open();
        restarted.Start(new CancellationToken(true));
        Assert.Equal("failed", restarted.Snapshot().Single(j => j.Id == "recover").Status);
        Assert.Equal(pause, restarted.PausedUntilUtc);
    }

    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 8)]
    public void Legacy_quota_migration_requires_quota_evidence_receipts_and_recent_class(bool quota, bool catalogue, int age)
    {
        var config = new ConfigStore(Path.Combine(_dir, "guard-config.json"));
        config.Save(AppConfig.CreateDefault());
        var valid = Path.Combine(_dir, "guard-valid.mp4");
        File.WriteAllBytes(valid, Mp4(1, true));
        var now = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        var job = new UploadJob("failed", valid, "clip", DateOnly.FromDateTime(now).AddDays(-age), 1, "failed", 5, null);
        var file = Path.Combine(_dir, "guard-queue.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new {
            uploadLimitUntilUtc = quota ? (DateTimeOffset?)new DateTimeOffset(now).AddHours(1) : null, jobs = new[] { job } }));
        var queue = new UploadQueue(new Uploader(), config, new LogService(), file, () => now, Task.Delay,
            int.MaxValue, 5, assetCatalog: catalogue ? new PeriodAssetCatalog(Path.Combine(_dir, "guard-assets.json")) : null);
        queue.Start(new CancellationToken(true));
        Assert.Equal("failed", Assert.Single(queue.Snapshot()).Status);
    }

    private sealed class Uploader : IYouTubeUploadService
    {
        public int Calls;
        public Task<string> UploadAsync(string path, string title, string privacy, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult("new-video"); }
    }

    private static byte[] Mp4(int samples, bool data)
    {
        static byte[] Box(string name, byte[] body) =>
            BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(body.Length + 8))
                .Concat(Encoding.ASCII.GetBytes(name)).Concat(body).ToArray();
        var table = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(8), samples);
        return Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsz", table))))))
            .Concat(Box("mdat", data ? new byte[] { 1, 2, 3 } : [])).ToArray();
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
