using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Keeps the rolling weekday playlist window present and backfills locally known uploaded videos.
/// Also reconciles ended live archives after the last school period, including missed work
/// from a previous boot. Live work polls at most every 15 minutes without an interactive login.
/// </summary>
public sealed class DailyPlaylistCoordinator
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeOnly DailyRunTime = new(8, 0);

    private readonly IYouTubePlaylistService _playlists;
    private readonly IPeriodAssetCatalog _assets;
    private readonly IUploadQueue _uploads;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeZoneInfo _koreaTimeZone;
    private readonly object _gate = new();
    private Task? _worker;
    private readonly ILiveReplayCatalog? _replays;
    private readonly IPeriodScheduleStore? _schedules;
    private readonly IYouTubeReplayGateway? _replayGateway;
    private int _replayCursor;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);

    public DailyPlaylistCoordinator(
        IYouTubePlaylistService playlists,
        IPeriodAssetCatalog assets,
        IUploadQueue uploads,
        IConfigStore configStore,
        ILogService log,
        ILiveReplayCatalog replays,
        IPeriodScheduleStore schedules,
        IYouTubeReplayGateway replayGateway)
        : this(playlists, assets, uploads, configStore, log,
            () => DateTimeOffset.UtcNow, Task.Delay, ResolveKoreaTimeZone(), replays, schedules, replayGateway) { }

    public DailyPlaylistCoordinator(
        IYouTubePlaylistService playlists,
        IPeriodAssetCatalog assets,
        IUploadQueue uploads,
        IConfigStore configStore,
        ILogService log,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeZoneInfo koreaTimeZone,
        ILiveReplayCatalog? replays = null,
        IPeriodScheduleStore? schedules = null,
        IYouTubeReplayGateway? replayGateway = null)
    {
        _playlists = playlists;
        _assets = assets;
        _uploads = uploads;
        _configStore = configStore;
        _log = log;
        _utcNow = utcNow;
        _delay = delay;
        _koreaTimeZone = koreaTimeZone;
        _replays = replays;
        _schedules = schedules;
        _replayGateway = replayGateway;
    }

    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            _worker ??= RunAsync(ct);
        }
    }

    public async Task RunMaintenanceAsync(CancellationToken ct)
    {
        await _maintenanceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MaintainPlaylistsAsync(ct).ConfigureAwait(false);
            await MaintainReplaysAsync(ct).ConfigureAwait(false);
        }
        finally { _maintenanceGate.Release(); }
    }

    private async Task MaintainPlaylistsAsync(CancellationToken ct)
    {
        var room = ResolveRoom();
        var localToday = DateOnly.FromDateTime(KoreaNow().DateTime);
        for (var offset = -7; offset <= 7; offset++)
        {
            var date = localToday.AddDays(offset);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            await _playlists.EnsurePlaylistAsync(room, date, ct).ConfigureAwait(false);
        }

        var videos = _assets.Snapshot()
            .Where(asset => !string.IsNullOrWhiteSpace(asset.VideoId))
            .Select(asset => (asset.Date, VideoId: asset.VideoId!))
            .Concat(_uploads.Snapshot()
                .Where(job => job.Status == UploadJobStatus.Completed && !string.IsNullOrWhiteSpace(job.VideoId))
                .Select(job => (job.Date, VideoId: job.VideoId!)))
            .Distinct()
            .OrderBy(item => item.Date)
            .ThenBy(item => item.VideoId, StringComparer.Ordinal)
            .ToArray();

        foreach (var (date, videoId) in videos)
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            await _playlists.AddVideoAsync(room, date, videoId, ct).ConfigureAwait(false);
        }
    }

    private async Task MaintainReplaysAsync(CancellationToken ct)
    {
        if (!_configStore.Load().YouTube.UseLocalLiveReplayRecovery) return;
        if (_replays is null || _schedules is null || _replayGateway is null) return;
        var room = ResolveRoom();
        var pending = _replays.Snapshot().Where(item => item.PlaylistAddedAtUtc is null
            && (string.IsNullOrWhiteSpace(item.Room)
                || string.Equals(item.Room, room, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.StartedAtUtc).ToArray();
        if (pending.Length == 0) return;
        var start = _replayCursor % pending.Length;
        var count = Math.Min(32, pending.Length);
        var budget = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.Elapsed > TimeSpan.FromMinutes(2)) break;
            _replayCursor = (start + i + 1) % pending.Length;
            var replay = pending[(start + i) % pending.Length];
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(replay.StartedAtUtc, _koreaTimeZone).DateTime);
            var periods = _schedules.ResolveForDate(date).Periods;
            if (periods.Count == 0 || KoreaNow().DateTime < date.ToDateTime(periods.Max(p => p.End))) continue;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                // The playlist membership store repairs a crash after insertion but before this mark.
                if (!_playlists.IsVideoAdded(room, date, replay.BroadcastId))
                {
                    var endedAt = await _replayGateway.GetCompletedReplayEndAsync(
                        replay.BroadcastId, !_replays.IsCurrentSession(replay.BroadcastId), attempt.Token).ConfigureAwait(false);
                    if (endedAt is null) continue;
                    _replays.RecordEnded(replay.BroadcastId, endedAt.Value);
                    await _playlists.AddVideoAsync(room, date, replay.BroadcastId, attempt.Token).ConfigureAwait(false);
                }
                // AddVideoAsync records API failures instead of throwing. Never mark those as done.
                if (_playlists.IsVideoAdded(room, date, replay.BroadcastId))
                    _replays.MarkPlaylistAdded(replay.BroadcastId, _utcNow());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"라이브 재생목록 편입 대기: {date:yyyy-MM-dd} / {replay.BroadcastId} ({ex.GetType().Name}).");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await RunSafelyAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var now = KoreaNow();
            var nextDaily = DateOnly.FromDateTime(now.DateTime).ToDateTime(DailyRunTime);
            if (nextDaily <= now.DateTime) nextDaily = nextDaily.AddDays(1);
            var wait = nextDaily - now.DateTime;
            if ((_replays is not null || _playlists.HasRetryableFailures) && wait > RetryInterval)
                wait = RetryInterval;
            if (_schedules is not null)
            {
                var periods = _schedules.ResolveForDate(DateOnly.FromDateTime(now.DateTime)).Periods;
                if (periods.Count > 0)
                {
                    var untilEnd = DateOnly.FromDateTime(now.DateTime).ToDateTime(periods.Max(p => p.End)) - now.DateTime;
                    if (untilEnd > TimeSpan.Zero && untilEnd < wait) wait = untilEnd;
                }
            }

            try
            {
                await _delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunSafelyAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            await RunMaintenanceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error("날짜별 재생목록 자동 보장 작업 중 오류", ex);
        }
    }

    private DateTimeOffset KoreaNow() => TimeZoneInfo.ConvertTime(_utcNow(), _koreaTimeZone);

    private string ResolveRoom()
    {
        var room = TitleTemplater.ResolveRoomName(_configStore.Load());
        return string.IsNullOrWhiteSpace(room) ? Environment.MachineName.ToLowerInvariant() : room;
    }

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        foreach (var id in new[] { "Korea Standard Time", "Asia/Seoul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("SilentStream-KST", TimeSpan.FromHours(9), "KST", "KST");
    }
}
