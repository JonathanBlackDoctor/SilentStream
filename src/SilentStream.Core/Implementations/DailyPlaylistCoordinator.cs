using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Keeps the rolling weekday playlist window present and backfills locally known uploaded videos.
/// It runs immediately at application start, at 08:00 Korea time, and every 15 minutes while a
/// playlist remains in a retryable state.
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

    public DailyPlaylistCoordinator(
        IYouTubePlaylistService playlists,
        IPeriodAssetCatalog assets,
        IUploadQueue uploads,
        IConfigStore configStore,
        ILogService log)
        : this(playlists, assets, uploads, configStore, log,
            () => DateTimeOffset.UtcNow, Task.Delay, ResolveKoreaTimeZone()) { }

    public DailyPlaylistCoordinator(
        IYouTubePlaylistService playlists,
        IPeriodAssetCatalog assets,
        IUploadQueue uploads,
        IConfigStore configStore,
        ILogService log,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeZoneInfo koreaTimeZone)
    {
        _playlists = playlists;
        _assets = assets;
        _uploads = uploads;
        _configStore = configStore;
        _log = log;
        _utcNow = utcNow;
        _delay = delay;
        _koreaTimeZone = koreaTimeZone;
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

    private async Task RunAsync(CancellationToken ct)
    {
        await RunSafelyAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var now = KoreaNow();
            var nextDaily = DateOnly.FromDateTime(now.DateTime).ToDateTime(DailyRunTime);
            if (nextDaily <= now.DateTime) nextDaily = nextDaily.AddDays(1);
            var wait = nextDaily - now.DateTime;
            if (_playlists.HasRetryableFailures && wait > RetryInterval) wait = RetryInterval;

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
