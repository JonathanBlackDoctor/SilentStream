using System.Net;
using Google;
using Google.Apis.Requests;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class PlaylistQuotaRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-playlist-quota-").FullName;
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-30T04:00:00Z");
    private readonly Gateway _gateway = new();
    private static readonly DateOnly Date = new(2026, 10, 29);
    private YouTubePlaylistService Service() => new(_gateway, new LogService(),
        Path.Combine(_dir, "playlists.json"), () => _now);

    [Theory]
    [InlineData("find")]
    [InlineData("create")]
    [InlineData("contains")]
    [InlineData("add")]
    public async Task Daily_quota_stops_other_dates_and_membership_calls_across_restart(string failureStage)
    {
        var service = Service();
        await service.AddVideoAsync("m112", Date, "known-video", default);
        _gateway.FailureStage = failureStage;
        if (failureStage is "find" or "create")
            await service.EnsurePlaylistAsync("m112", Date.AddDays(1), default);
        else
            await service.AddVideoAsync("m112", Date, "pending-video", default);
        var stoppedAt = _gateway.Calls;

        await service.EnsurePlaylistAsync("m112", Date.AddDays(2), default);
        await service.AddVideoAsync("m112", Date, "pending-video", default);
        Assert.Equal(stoppedAt, _gateway.Calls);

        service = Service(); // The retry deadline must survive a process restart.
        Assert.True(service.IsVideoAdded("m112", Date, "known-video"));
        Assert.False(service.IsVideoAdded("m112", Date, "pending-video"));
        await service.EnsurePlaylistAsync("m112", Date.AddDays(2), default);
        await service.AddVideoAsync("m112", Date, "pending-video", default);
        Assert.Equal(stoppedAt, _gateway.Calls);

        _now = DateTimeOffset.Parse("2026-10-30T07:00:01Z");
        _gateway.FailureStage = null;
        await service.EnsurePlaylistAsync("m112", Date.AddDays(2), default);
        await service.AddVideoAsync("m112", Date, "pending-video", default);
        Assert.True(service.IsVideoAdded("m112", Date, "known-video"));
        Assert.True(service.IsVideoAdded("m112", Date, "pending-video"));
        Assert.Equal(2, _gateway.RemoteIds.Count);
    }

    [Theory]
    [InlineData("2026-10-30T04:00:00Z", "2026-10-30T07:00:01Z")]
    [InlineData("2026-11-01T07:30:00Z", "2026-11-02T08:00:01Z")]
    [InlineData("2026-03-08T07:00:00Z", "2026-03-08T08:00:01Z")]
    [InlineData("2026-03-08T08:30:00Z", "2026-03-09T07:00:01Z")]
    public async Task Retry_uses_next_Pacific_midnight_including_DST_changes(string now, string reset)
    {
        _now = DateTimeOffset.Parse(now);
        _gateway.FailureStage = "find";
        _gateway.Reason = "dailyLimitExceeded";
        await Service().EnsurePlaylistAsync("m112", Date, default);
        var stoppedAt = _gateway.Calls;
        _gateway.FailureStage = null;
        _now = DateTimeOffset.Parse(reset).AddTicks(-1);
        await Service().EnsurePlaylistAsync("m112", Date, default);
        Assert.Equal(stoppedAt, _gateway.Calls);
        _now = DateTimeOffset.Parse(reset);
        var result = await Service().EnsurePlaylistAsync("m112", Date, default);
        Assert.Equal(DailyPlaylistStatus.Ready, result.Status);
        Assert.True(_gateway.Calls > stoppedAt);
    }

    [Theory]
    [InlineData(403, "forbidden")]
    [InlineData(403, "uploadLimitExceeded")]
    [InlineData(429, "rateLimitExceeded")]
    [InlineData(500, "quotaExceeded")]
    public async Task Unrelated_failures_do_not_pause_all_playlists_until_daily_reset(int status, string reason)
    {
        _gateway.FailureStage = "find";
        _gateway.Status = (HttpStatusCode)status;
        _gateway.Reason = reason;
        var service = Service();
        await service.EnsurePlaylistAsync("m112", Date, default);
        await service.EnsurePlaylistAsync("m112", Date.AddDays(1), default);
        Assert.Equal(2, _gateway.Calls);
    }

    [Theory]
    [InlineData("find")]
    [InlineData("replay")]
    public async Task Maintenance_stops_the_batch_and_replay_polling_after_daily_quota(string stage)
    {
        _now = DateTimeOffset.Parse("2026-10-30T08:30:00Z");
        var catalog = new LiveReplayCatalog(Path.Combine(_dir, "replays.json"));
        catalog.RecordStarted(new LiveSession("live-1", "", "", ""), _now.AddDays(-1), "m112");
        catalog.RecordStarted(new LiveSession("live-2", "", "", ""), _now.AddDays(-1), "m112");
        var config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var value = AppConfig.CreateDefault();
        value.DeviceName = "m112";
        value.YouTube.UseLocalLiveReplayRecovery = true;
        config.Save(value);
        var service = Service();
        var coordinator = new DailyPlaylistCoordinator(service, new Assets(), new Queue(), config,
            new LogService(), () => _now, Task.Delay,
            TimeZoneInfo.CreateCustomTimeZone("quota-kst", TimeSpan.FromHours(9), "KST", "KST"),
            catalog, new PeriodScheduleStore(), _gateway);
        _gateway.FailureStage = stage;
        await coordinator.RunMaintenanceAsync(default);
        if (stage == "find")
        {
            Assert.Equal(1, _gateway.Calls);
            Assert.Equal(0, _gateway.ReplayCalls);
        }
        else Assert.Equal(1, _gateway.ReplayCalls);
        var stoppedAt = _gateway.Calls;
        await coordinator.RunMaintenanceAsync(default);
        Assert.Equal(stoppedAt, _gateway.Calls);
        Assert.All(catalog.Snapshot(), replay => Assert.Null(replay.PlaylistAddedAtUtc));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Gateway : IYouTubePlaylistGateway, IYouTubeReplayGateway
    {
        public string? FailureStage;
        public string Reason = "quotaExceeded";
        public HttpStatusCode Status = HttpStatusCode.Forbidden;
        public int Calls;
        public int ReplayCalls;
        public HashSet<string> RemoteIds { get; } = [];
        private void Call(string stage)
        {
            Calls++;
            if (stage == FailureStage)
                throw new GoogleApiException("youtube", Reason)
                {
                    HttpStatusCode = Status,
                    Error = new RequestError { Errors = [new SingleError { Reason = Reason }] }
                };
        }
        public Task<string?> FindPlaylistByMarkerAsync(string marker, CancellationToken ct)
        {
            Call("find");
            return Task.FromResult<string?>(FailureStage == "create" ? null : "PL-known");
        }
        public Task<string> CreatePlaylistAsync(string title, string description, CancellationToken ct)
        {
            Call("create");
            return Task.FromResult("PL-created");
        }
        public Task<bool> ContainsVideoAsync(string playlistId, string videoId, CancellationToken ct)
        {
            Call("contains");
            return Task.FromResult(RemoteIds.Contains(videoId));
        }
        public Task AddVideoAsync(string playlistId, string videoId, CancellationToken ct)
        {
            Call("add");
            RemoteIds.Add(videoId);
            return Task.CompletedTask;
        }
        public Task<DateTimeOffset?> GetCompletedReplayEndAsync(string broadcastId, bool allowTransition, CancellationToken ct)
        {
            ReplayCalls++;
            Call("replay");
            return Task.FromResult<DateTimeOffset?>(DateTimeOffset.Parse("2026-10-29T08:25:00Z"));
        }
    }

    private sealed class Assets : IPeriodAssetCatalog
    {
        public PeriodAsset Upsert(PeriodAsset asset) => asset;
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
