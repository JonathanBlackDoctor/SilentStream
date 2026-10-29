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
    public async Task Invalid_local_media_fails_once_preserves_file_and_does_not_block_next_job()
    {
        var config = new ConfigStore(Path.Combine(_dir, "invalid-config.json"));
        config.Save(AppConfig.CreateDefault());
        var invalid = Path.Combine(_dir, "invalid.mp4");
        var valid = Path.Combine(_dir, "valid-next.mp4");
        File.WriteAllBytes(invalid, new byte[1217]);
        File.WriteAllBytes(valid, Mp4(3, true));
        var uploader = new InvalidThenValidUploader(invalid);
        var queue = new UploadQueue(uploader, config, new LogService(), Path.Combine(_dir, "invalid-queue.json"),
            () => DateTime.Now, Task.Delay, int.MaxValue, 5);
        queue.Enqueue(new UploadJob("invalid", invalid, "bad", DateOnly.FromDateTime(DateTime.Now), 1, "pending", 0, null));
        queue.Enqueue(new UploadJob("valid-next", valid, "good", DateOnly.FromDateTime(DateTime.Now), 2, "pending", 0, null));
        using var stop = new CancellationTokenSource();
        queue.Start(stop.Token);
        for (var i = 0; i < 200 && queue.Snapshot().Single(j => j.Id == "valid-next").Status != "completed"; i++)
            await Task.Delay(10);
        stop.Cancel();
        Assert.Equal(1, uploader.InvalidCalls);
        Assert.Equal("failed", queue.Snapshot().Single(j => j.Id == "invalid").Status);
        Assert.Equal("completed", queue.Snapshot().Single(j => j.Id == "valid-next").Status);
        Assert.True(File.Exists(invalid));
    }

    private sealed class InvalidThenValidUploader(string invalid) : IYouTubeUploadService
    {
        public int InvalidCalls;
        public Task<string> UploadAsync(string path, string title, string privacy, CancellationToken ct)
        {
            if (path != invalid) return Task.FromResult("valid-video");
            Interlocked.Increment(ref InvalidCalls);
            throw new InvalidDataException("no video samples");
        }
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
        Assert.Equal(2, saved["quotaRecoveryVersion"]!.GetValue<int>());
        saved["jobs"]!.AsArray().Single(j => j!["id"]!.GetValue<string>() == "recover")!["status"] = "failed";
        File.WriteAllText(file, saved.ToJsonString());
        var restarted = Open();
        restarted.Start(new CancellationToken(true));
        Assert.Equal("failed", restarted.Snapshot().Single(j => j.Id == "recover").Status);
        Assert.Equal(pause, restarted.PausedUntilUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Delayed_reboot_repairs_missed_failures_without_touching_newer_work(int migrationVersion)
    {
        var config = new ConfigStore(Path.Combine(_dir, "delayed-config.json"));
        config.Save(AppConfig.CreateDefault());
        var valid = Path.Combine(_dir, "delayed-valid.mp4");
        File.WriteAllBytes(valid, Mp4(1, true));
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        var observed = new DateTimeOffset(2026, 10, 29, 7, 53, 46, TimeSpan.Zero);
        var job = new UploadJob("missed", valid, "clip", new DateOnly(2026, 10, 28), 6,
            UploadJobStatus.Failed, 5, null,
            UploadStartedAtUtc: new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero));
        var pending = job with { Id = "newer-pending", Date = job.Date.AddDays(1), Status = "pending", Attempts = 0 };
        var jobs = new[] { job, pending,
            job with { Id = "older", Date = job.Date.AddDays(-1) },
            job with { Id = "newer-failure", Date = job.Date.AddDays(1), UploadStartedAtUtc = observed.AddHours(1) },
            job with { Id = "already-retried", UploadStartedAtUtc = observed.AddHours(1) },
            job with { Id = "retried-before-renewed-pause", UploadStartedAtUtc = observed.AddHours(-1) },
            job with { Id = "unknown-attempt", UploadStartedAtUtc = null },
            job with { Id = "incomplete-budget", Attempts = 1 },
            job with { Id = "queue-receipt", Status = "completed", VideoId = "existing", SourceKey = "known-source" },
            job with { Id = "receipt-copy", SourceKey = "known-source" } };
        var file = Path.Combine(_dir, "delayed-queue.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        { quotaRecoveryVersion = migrationVersion, uploadLimitUntilUtc = observed.AddHours(24), jobs }));
        var assets = new PeriodAssetCatalog(Path.Combine(_dir, "delayed-assets.json"));
        var uploader = new Uploader();
        var queue = new UploadQueue(uploader, config, new LogService(), file, () => now, Task.Delay,
            int.MaxValue, 5, assetCatalog: assets);
        queue.Start(new CancellationToken(true));
        var snapshot = queue.Snapshot();
        Assert.Equal("pending", snapshot.Single(j => j.Id == "missed").Status);
        Assert.Equal(0, snapshot.Single(j => j.Id == "missed").Attempts);
        Assert.Null(snapshot.Single(j => j.Id == "missed").UploadStartedAtUtc);
        Assert.Equal(pending, snapshot.Single(j => j.Id == pending.Id));
        foreach (var id in new[] { "older", "newer-failure", "already-retried", "incomplete-budget", "receipt-copy" })
            Assert.Equal("failed", snapshot.Single(j => j.Id == id).Status);
        Assert.Equal(migrationVersion == 0 ? "pending" : "failed", snapshot.Single(j => j.Id == "unknown-attempt").Status);
        Assert.Equal(migrationVersion == 0 ? "pending" : "failed", snapshot.Single(j => j.Id == "retried-before-renewed-pause").Status);
        Assert.Equal("existing", snapshot.Single(j => j.Id == "queue-receipt").VideoId);
        Assert.Equal(observed.AddHours(24), queue.PausedUntilUtc);
        Assert.Equal(0, uploader.Calls);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(2, saved["quotaRecoveryVersion"]!.GetValue<int>());
        var repaired = saved["jobs"]!.AsArray().Single(j => j!["id"]!.GetValue<string>() == "missed")!;
        repaired["status"] = "failed";
        repaired["attempts"] = 5;
        File.WriteAllText(file, saved.ToJsonString());
        var restarted = new UploadQueue(uploader, config, new LogService(), file, () => now.AddDays(1), Task.Delay,
            int.MaxValue, 5, assetCatalog: assets);
        restarted.Start(new CancellationToken(true));
        Assert.Equal("failed", restarted.Snapshot().Single(j => j.Id == "missed").Status);
        Assert.Equal(5, restarted.Snapshot().Single(j => j.Id == "missed").Attempts);
        Assert.Equal(observed.AddHours(24), System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!["uploadLimitUntilUtc"]!.GetValue<DateTimeOffset>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Recovery_does_not_fall_back_to_older_days_when_latest_failure_has_no_media(int version)
    {
        var config = new ConfigStore(Path.Combine(_dir, "no-fallback-config.json"));
        config.Save(AppConfig.CreateDefault());
        var valid = Path.Combine(_dir, "older-valid.mp4");
        File.WriteAllBytes(valid, Mp4(1, true));
        var now = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
        var job = new UploadJob("latest-missing", Path.Combine(_dir, "missing.mp4"), "clip", new DateOnly(2026, 10, 28),
            1, "failed", 5, null, UploadStartedAtUtc: new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
        var file = Path.Combine(_dir, "no-fallback-queue.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            quotaRecoveryVersion = version, uploadLimitUntilUtc = new DateTimeOffset(now).AddHours(-1),
            jobs = new[] { job, job with { Id = "older", Date = job.Date.AddDays(-1), FilePath = valid } }
        }));
        var queue = new UploadQueue(new Uploader(), config, new LogService(), file, () => now, Task.Delay,
            int.MaxValue, 5, assetCatalog: new PeriodAssetCatalog(Path.Combine(_dir, "no-fallback-assets.json")));
        queue.Start(new CancellationToken(true));
        Assert.All(queue.Snapshot(), j => Assert.Equal("failed", j.Status));
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
            uploadLimitUntilUtc = quota ? (DateTimeOffset?)new DateTimeOffset(now).AddHours(24) : null, jobs = new[] { job } }));
        var queue = new UploadQueue(new Uploader(), config, new LogService(), file, () => now, Task.Delay,
            int.MaxValue, 5, assetCatalog: catalogue ? new PeriodAssetCatalog(Path.Combine(_dir, "guard-assets.json")) : null);
        queue.Start(new CancellationToken(true));
        Assert.Equal("failed", Assert.Single(queue.Snapshot()).Status);
        if (quota && !catalogue)
        {
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Null(saved["quotaRecoveryVersion"]);
            var available = new UploadQueue(new Uploader(), config, new LogService(), file, () => now, Task.Delay,
                int.MaxValue, 5, assetCatalog: new PeriodAssetCatalog(Path.Combine(_dir, "available-assets.json")));
            available.Start(new CancellationToken(true));
            Assert.Equal("pending", Assert.Single(available.Snapshot()).Status);
        }
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
        return Box("moov", Mp4VideoValidationTests.Track(1, "vide", (uint)samples))
            .Concat(Box("mdat", data ? new byte[4096] : [])).ToArray();
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
