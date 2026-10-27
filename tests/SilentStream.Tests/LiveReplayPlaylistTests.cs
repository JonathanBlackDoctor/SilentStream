using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class LiveReplayPlaylistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-replay-playlist-").FullName;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-26T23:00:00Z"); // 10/27 08:00 KST
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-27T08:25:00Z"); // 17:25 KST
    private readonly Gateway _gateway = new();
    private LiveReplayCatalog Catalog => new(Path.Combine(_dir, "replays.json"));
    private YouTubePlaylistService Playlists => new(_gateway, new LogService(), Path.Combine(_dir, "playlists.json"), () => _now);

    private DailyPlaylistCoordinator Coordinator(LiveReplayCatalog catalog, YouTubePlaylistService? playlists = null)
    {
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var value = AppConfig.CreateDefault(); value.DeviceName = "m112";
        value.YouTube.UseLocalLiveReplayRecovery = true; config.Save(value);
        return new DailyPlaylistCoordinator(playlists ?? Playlists, new Assets(), new Queue(), config,
            new LogService(), () => _now, Task.Delay,
            TimeZoneInfo.CreateCustomTimeZone("test-kst", TimeSpan.FromHours(9), "KST", "KST"),
            catalog, new PeriodScheduleStore(), _gateway);
    }

    private static void Add(LiveReplayCatalog catalog, string id = "live-1", string room = "m112", DateTimeOffset? start = null)
        => catalog.RecordStarted(new LiveSession(id, "ingest-secret", "rtmp://invalid", "https://youtu.be/" + id), start ?? Start, room);

    [Fact]
    public async Task Ended_broadcast_waits_until_the_last_class_in_Korea()
    {
        var catalog = Catalog; Add(catalog); catalog.RecordEnded("live-1", Start.AddHours(2));
        _now = DateTimeOffset.Parse("2026-10-27T08:24:59Z");
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Empty(_gateway.ReplayChecks); Assert.Equal(0, _gateway.Inserts);
        _now = _now.AddSeconds(1);
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Equal(1, _gateway.Inserts);
        Assert.NotNull(Catalog.Latest!.PlaylistAddedAtUtc);
        Assert.True(Playlists.IsVideoAdded("m112", new DateOnly(2026, 10, 27), "live-1"));
    }

    [Fact]
    public async Task Current_live_is_not_stopped_or_added_even_after_last_class()
    {
        var catalog = Catalog; Add(catalog); _gateway.End = null;
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Equal(("live-1", false), Assert.Single(_gateway.ReplayChecks));
        Assert.Null(Catalog.Latest!.PlaylistAddedAtUtc); Assert.Equal(0, _gateway.Inserts);
    }

    [Fact]
    public async Task Abrupt_power_loss_recovers_original_date_on_next_boot_and_waits_for_archive()
    {
        Add(Catalog);
        _now = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
        _gateway.End = null;
        var coordinator = Coordinator(Catalog);
        await coordinator.RunMaintenanceAsync(default);
        Assert.Equal(("live-1", true), Assert.Single(_gateway.ReplayChecks));
        Assert.Null(Catalog.Latest!.PlaylistAddedAtUtc);
        _gateway.End = Start.AddHours(9);
        await coordinator.RunMaintenanceAsync(default);
        Assert.True(Playlists.IsVideoAdded("m112", new DateOnly(2026, 10, 27), "live-1"));
        Assert.False(Playlists.IsVideoAdded("m112", new DateOnly(2026, 10, 28), "live-1"));
        Assert.NotNull(Catalog.Latest!.PlaylistAddedAtUtc);
    }

    [Fact]
    public async Task Insert_failure_is_pending_then_retried_once_without_duplicate()
    {
        var catalog = Catalog; Add(catalog); catalog.RecordEnded("live-1", Start.AddHours(1));
        _gateway.FailInsert = true;
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Null(Catalog.Latest!.PlaylistAddedAtUtc);
        _gateway.FailInsert = false;
        await Coordinator(Catalog).RunMaintenanceAsync(default);
        await Coordinator(Catalog).RunMaintenanceAsync(default);
        Assert.Equal(1, _gateway.Inserts); Assert.NotNull(Catalog.Latest!.PlaylistAddedAtUtc);
    }

    [Fact]
    public async Task Crash_after_membership_save_before_catalog_mark_repairs_without_an_API_call()
    {
        Add(Catalog);
        await Playlists.AddVideoAsync("m112", new DateOnly(2026, 10, 27), "live-1", default);
        await Coordinator(Catalog).RunMaintenanceAsync(default);
        Assert.Empty(_gateway.ReplayChecks); Assert.Equal(1, _gateway.Inserts);
        Assert.NotNull(Catalog.Latest!.PlaylistAddedAtUtc);
    }

    [Fact]
    public async Task Remote_success_before_lost_local_save_is_checked_before_reinsertion()
    {
        Add(Catalog); _gateway.RemoteIds.Add("live-1");
        await Coordinator(Catalog).RunMaintenanceAsync(default);
        Assert.Equal(0, _gateway.Inserts); Assert.NotNull(Catalog.Latest!.PlaylistAddedAtUtc);
    }

    [Fact]
    public async Task Different_room_and_weekend_are_not_associated()
    {
        var catalog = Catalog; Add(catalog, "other-room", "m111");
        Add(catalog, "weekend", start: DateTimeOffset.Parse("2026-10-24T00:00:00Z"));
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Empty(_gateway.ReplayChecks); Assert.Equal(0, _gateway.Inserts);
    }

    [Fact]
    public async Task One_unavailable_replay_does_not_block_another()
    {
        var catalog = Catalog; Add(catalog, "bad"); Add(catalog, "good");
        _gateway.FailedId = "bad";
        await Coordinator(catalog).RunMaintenanceAsync(default);
        Assert.Null(Catalog.Snapshot().Single(x => x.BroadcastId == "bad").PlaylistAddedAtUtc);
        Assert.NotNull(Catalog.Snapshot().Single(x => x.BroadcastId == "good").PlaylistAddedAtUtc);
    }

    [Fact]
    public void Pending_work_is_not_pruned_by_recent_link_history_limit()
    {
        var catalog = Catalog;
        for (var i = 0; i < 75; i++) Add(catalog, "live-" + i, start: Start.AddDays(-i));
        Assert.Equal(75, Catalog.Snapshot().Count);
        Assert.Equal("m112", Catalog.Latest!.Room);
        Assert.DoesNotContain("ingest-secret", File.ReadAllText(Path.Combine(_dir, "replays.json")));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Gateway : IYouTubePlaylistGateway, IYouTubeReplayGateway
    {
        public List<(string, bool)> ReplayChecks { get; } = [];
        public HashSet<string> RemoteIds { get; } = [];
        public DateTimeOffset? End = Start.AddHours(9);
        public bool FailInsert;
        public string? FailedId;
        public int Inserts;
        public Task<DateTimeOffset?> GetCompletedReplayEndAsync(string id, bool completeOrphan, CancellationToken ct)
        {
            ReplayChecks.Add((id, completeOrphan));
            if (id == FailedId) throw new IOException("offline");
            return Task.FromResult(End);
        }
        public Task<string?> FindPlaylistByMarkerAsync(string marker, CancellationToken ct) => Task.FromResult<string?>("PL-test");
        public Task<string> CreatePlaylistAsync(string title, string description, CancellationToken ct) => Task.FromResult("PL-test");
        public Task<bool> ContainsVideoAsync(string playlistId, string videoId, CancellationToken ct) => Task.FromResult(RemoteIds.Contains(videoId));
        public Task AddVideoAsync(string playlistId, string videoId, CancellationToken ct)
        {
            if (FailInsert) throw new IOException("offline");
            RemoteIds.Add(videoId); Inserts++; return Task.CompletedTask;
        }
    }
    private sealed class Assets : IPeriodAssetCatalog
    {
        public PeriodAsset Upsert(PeriodAsset item) => item;
        public IReadOnlyList<PeriodAsset> Snapshot() => [];
        public PeriodAsset? Find(string id) => null;
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkAudioPath(string id, string audioPath) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }
    private sealed class Queue : IUploadQueue
    {
        public void Enqueue(UploadJob job) { }
        public IReadOnlyList<UploadJob> Snapshot() => [];
        public void Start(CancellationToken ct) { }
    }
}
