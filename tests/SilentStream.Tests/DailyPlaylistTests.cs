using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class YouTubePlaylistServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-playlist-").FullName;
    private string StateFile => Path.Combine(_dir, "daily_playlists.json");

    [Fact]
    public async Task Ensure_recovers_a_remote_playlist_after_crash_before_local_save()
    {
        var date = new DateOnly(2026, 9, 4);
        File.WriteAllText(StateFile, """
            { "items": [ { "room": "m111", "date": "2026-09-04",
              "title": "m111 | 2026-09-04", "playlistId": null, "url": null,
              "status": "creating", "lastAttemptAtUtc": "2026-09-03T23:00:00Z", "error": null } ],
              "addedVideoIds": {} }
            """);
        var gateway = new FakeGateway { FoundPlaylistId = "PL-existing" };
        var service = new YouTubePlaylistService(
            gateway, new LogService(), StateFile, () => DateTimeOffset.Parse("2026-09-03T23:01:00Z"));

        var item = await service.EnsurePlaylistAsync("m111", date, CancellationToken.None);

        Assert.Equal(DailyPlaylistStatus.Ready, item.Status);
        Assert.Equal("PL-existing", item.PlaylistId);
        Assert.Equal(0, gateway.CreateCalls);
        Assert.Contains("list=PL-existing", item.Url);
    }

    [Fact]
    public async Task Video_membership_is_checked_and_recorded_idempotently()
    {
        var gateway = new FakeGateway { FoundPlaylistId = "PL1" };
        var service = new YouTubePlaylistService(gateway, new LogService(), StateFile, () => DateTimeOffset.UtcNow);
        var date = new DateOnly(2026, 9, 4);

        await service.AddVideoAsync("m111", date, "video-1", CancellationToken.None);
        await service.AddVideoAsync("m111", date, "video-1", CancellationToken.None);

        Assert.Equal(1, gateway.ContainsCalls);
        Assert.Equal(1, gateway.AddCalls);

        // Simulate a process restart: the durable local membership record avoids another API insert.
        var restarted = new YouTubePlaylistService(gateway, new LogService(), StateFile, () => DateTimeOffset.UtcNow);
        await restarted.AddVideoAsync("m111", date, "video-1", CancellationToken.None);
        Assert.Equal(1, gateway.AddCalls);
    }

    [Fact]
    public async Task Authorization_failure_is_persisted_for_the_inspection_page()
    {
        var gateway = new FakeGateway { AuthorizationRequired = true };
        var service = new YouTubePlaylistService(gateway, new LogService(), StateFile, () => DateTimeOffset.UtcNow);

        var item = await service.EnsurePlaylistAsync("m111", new DateOnly(2026, 9, 4), CancellationToken.None);

        Assert.Equal(DailyPlaylistStatus.AuthRequired, item.Status);
        Assert.True(service.HasRetryableFailures);
        Assert.Contains("auth-required", File.ReadAllText(StateFile));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeGateway : IYouTubePlaylistGateway
    {
        public string? FoundPlaylistId;
        public bool AuthorizationRequired;
        public int CreateCalls;
        public int ContainsCalls;
        public int AddCalls;

        public Task<string?> FindPlaylistByMarkerAsync(string marker, CancellationToken ct)
        {
            if (AuthorizationRequired) throw new YouTubeAuthorizationRequiredException("login required");
            return Task.FromResult(FoundPlaylistId);
        }

        public Task<string> CreatePlaylistAsync(string title, string description, CancellationToken ct)
        {
            CreateCalls++;
            Assert.Contains($"SilentStream-Playlist-Key: m111/2026-09-04", description);
            return Task.FromResult("PL-created");
        }

        public Task<bool> ContainsVideoAsync(string playlistId, string videoId, CancellationToken ct)
        {
            ContainsCalls++;
            return Task.FromResult(false);
        }

        public Task AddVideoAsync(string playlistId, string videoId, CancellationToken ct)
        {
            AddCalls++;
            return Task.CompletedTask;
        }
    }
}

public class DailyPlaylistCoordinatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-playlist-coord-").FullName;

    [Fact]
    public async Task Maintenance_ensures_rolling_weekdays_and_backfills_only_local_video_ids()
    {
        var playlists = new FakePlaylistService();
        var assets = new FakeAssets();
        assets.Assets.Add(new PeriodAsset("a", new DateOnly(2026, 9, 3), 1, "one", VideoId: "video-a"));
        assets.Assets.Add(new PeriodAsset("w", new DateOnly(2026, 9, 5), 1, "weekend", VideoId: "skip"));
        var uploads = new FakeQueue();
        uploads.Jobs.Add(new UploadJob("q", "x", "two", new DateOnly(2026, 9, 4), 0,
            UploadJobStatus.Completed, 0, "video-lunch", "lunch", "점심시간", ScheduleSlotKinds.Lunch, 5));
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var value = AppConfig.CreateDefault();
        value.DeviceName = "m111";
        config.Save(value);
        var coordinator = new DailyPlaylistCoordinator(
            playlists, assets, uploads, config, new LogService(),
            () => DateTimeOffset.Parse("2026-09-03T23:00:00Z"),
            (_, _) => Task.CompletedTask,
            TimeZoneInfo.CreateCustomTimeZone("test-kst", TimeSpan.FromHours(9), "KST", "KST"));

        await coordinator.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(11, playlists.Ensured.Count);
        Assert.All(playlists.Ensured, item =>
            Assert.DoesNotContain(item.Date.DayOfWeek, new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }));
        Assert.All(playlists.Ensured, item => Assert.InRange(item.Date,
            new DateOnly(2026, 8, 28), new DateOnly(2026, 9, 11)));
        Assert.Contains(playlists.Added, item => item.Date == new DateOnly(2026, 9, 3) && item.VideoId == "video-a");
        Assert.Contains(playlists.Added, item => item.Date == new DateOnly(2026, 9, 4) && item.VideoId == "video-lunch");
        Assert.DoesNotContain(playlists.Added, item => item.VideoId == "skip");
    }

    [Fact]
    public async Task Worker_runs_at_start_then_waits_until_next_korea_0800()
    {
        var playlists = new FakePlaylistService();
        var config = new ConfigStore(Path.Combine(_dir, "clock-config.json"));
        config.Save(AppConfig.CreateDefault());
        var delaySeen = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DailyPlaylistCoordinator(
            playlists, new FakeAssets(), new FakeQueue(), config, new LogService(),
            () => DateTimeOffset.Parse("2026-09-03T22:59:00Z"),
            (span, ct) =>
            {
                delaySeen.TrySetResult(span);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            TimeZoneInfo.CreateCustomTimeZone("test-kst-2", TimeSpan.FromHours(9), "KST", "KST"));
        using var cts = new CancellationTokenSource();

        coordinator.Start(cts.Token);
        var wait = await delaySeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.NotEmpty(playlists.Ensured); // startup pass
        Assert.Equal(TimeSpan.FromMinutes(1), wait); // 07:59 KST -> 08:00 KST
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakePlaylistService : IYouTubePlaylistService
    {
        public List<(string Room, DateOnly Date)> Ensured { get; } = [];
        public List<(string Room, DateOnly Date, string VideoId)> Added { get; } = [];
        public bool HasRetryableFailures { get; set; }
        public Task<DailyPlaylistRecord> EnsurePlaylistAsync(string room, DateOnly date, CancellationToken ct)
        {
            lock (Ensured) { Ensured.Add((room, date)); }
            return Task.FromResult(new DailyPlaylistRecord(room, date, $"{room} | {date:yyyy-MM-dd}",
                "PL", "https://example.test", DailyPlaylistStatus.Ready, DateTimeOffset.UtcNow, null));
        }
        public Task AddVideoAsync(string room, DateOnly date, string videoId, CancellationToken ct)
        {
            lock (Added) { Added.Add((room, date, videoId)); }
            return Task.CompletedTask;
        }
        public IReadOnlyList<DailyPlaylistRecord> Snapshot(string room, DateOnly from, DateOnly to) => [];
    }

    private sealed class FakeAssets : IPeriodAssetCatalog
    {
        public List<PeriodAsset> Assets { get; } = [];
        public PeriodAsset Upsert(PeriodAsset asset) { Assets.Add(asset); return asset; }
        public IReadOnlyList<PeriodAsset> Snapshot() => Assets.ToList();
        public PeriodAsset? Find(string id) => Assets.FirstOrDefault(item => item.Id == id);
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkAudioPath(string id, string audioPath) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }

    private sealed class FakeQueue : IUploadQueue
    {
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) => Jobs.Add(job);
        public IReadOnlyList<UploadJob> Snapshot() => Jobs.ToList();
        public void Start(CancellationToken ct) { }
    }
}

public class LegacyPendingSplitMigratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-split-migration-").FullName;

    [Fact]
    public async Task Pending_adjusted_or_merged_item_is_reprocessed_as_original_fixed_slots_once()
    {
        var date = new DateOnly(2026, 6, 15); // Monday
        var pending = new PendingSplit(
            "old-1", date, [1, 2],
            date.ToDateTime(new TimeOnly(9, 5)), date.ToDateTime(new TimeOnly(10, 5)),
            "session.mp4", date.ToDateTime(new TimeOnly(8, 20)), PendingSplitStatus.Approved,
            date.ToDateTime(new TimeOnly(10, 5)), null, null,
            date.ToDateTime(new TimeOnly(9, 10)), date.ToDateTime(new TimeOnly(10, 0)),
            null, null,
            [new RecordingSegment("session.mp4", date.ToDateTime(new TimeOnly(8, 20)), null)]);
        var stateFile = Path.Combine(_dir, "pending_splits.json");
        File.WriteAllText(stateFile, JsonSerializer.Serialize(new { splits = new[] { pending }, chain = (object?)null }));
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var value = AppConfig.CreateDefault();
        value.DeviceName = "m111";
        config.Save(value);
        var vod = new FakeVod(_dir);
        var queue = new FakeQueue();
        var assets = new FakeAssets();
        var migrator = new LegacyPendingSplitMigrator(
            vod, queue, config, new FakeAudio(), assets, new LogService(), stateFile);

        await migrator.RunAsync(CancellationToken.None);
        await migrator.RunAsync(CancellationToken.None);

        Assert.Equal(2, vod.Calls.Count);
        Assert.Equal((new TimeOnly(8, 25), new TimeOnly(9, 25)),
            (TimeOnly.FromDateTime(vod.Calls[0].Start), TimeOnly.FromDateTime(vod.Calls[0].End)));
        Assert.Equal((new TimeOnly(9, 25), new TimeOnly(10, 25)),
            (TimeOnly.FromDateTime(vod.Calls[1].Start), TimeOnly.FromDateTime(vod.Calls[1].End)));
        Assert.Equal(2, queue.Jobs.Count);
        Assert.Equal(["period-1", "period-2"], queue.Jobs.Select(job => job.ResolvedSlotId));
        Assert.Equal(2, assets.Assets.Count);
        Assert.Contains("\"status\": \"done\"", File.ReadAllText(stateFile));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeVod(string directory) : IVodSegmentService
    {
        public List<(DateTime Start, DateTime End, string Label)> Calls { get; } = [];
        public Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string?> ExtractRangeAsync(
            RecordingSession session, DateTime startLocal, DateTime endLocal,
            string fileBaseLabel, CancellationToken ct) =>
            Extract(startLocal, endLocal, fileBaseLabel);
        public Task<string?> ExtractRangeAsync(
            IReadOnlyList<RecordingSegment> segments, DateTime startLocal, DateTime endLocal,
            string fileBaseLabel, CancellationToken ct) =>
            Extract(startLocal, endLocal, fileBaseLabel);
        private Task<string?> Extract(DateTime start, DateTime end, string label)
        {
            Calls.Add((start, end, label));
            return Task.FromResult<string?>(Path.Combine(directory, label + ".mp4"));
        }
    }

    private sealed class FakeQueue : IUploadQueue
    {
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) => Jobs.Add(job);
        public IReadOnlyList<UploadJob> Snapshot() => Jobs.ToList();
        public void Start(CancellationToken ct) { }
    }

    private sealed class FakeAssets : IPeriodAssetCatalog
    {
        public List<PeriodAsset> Assets { get; } = [];
        public PeriodAsset Upsert(PeriodAsset asset)
        {
            Assets.RemoveAll(item => item.Id == asset.Id);
            Assets.Add(asset);
            return asset;
        }
        public IReadOnlyList<PeriodAsset> Snapshot() => Assets.ToList();
        public PeriodAsset? Find(string id) => Assets.FirstOrDefault(item => item.Id == id);
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkAudioPath(string id, string audioPath) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }

    private sealed class FakeAudio : ILocalAudioExportService
    {
        public Task<string?> ExportAsync(string sourceVideoPath, string assetId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }
}
