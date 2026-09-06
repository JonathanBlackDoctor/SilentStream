using System.Text.Json;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>Crash-safe room/date playlist state and idempotent YouTube mutations.</summary>
public sealed class YouTubePlaylistService : IYouTubePlaylistService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IYouTubePlaylistGateway _gateway;
    private readonly ILogService _log;
    private readonly string _stateFile;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private PlaylistState? _state;

    public YouTubePlaylistService(IYouTubePlaylistGateway gateway, ILogService log)
        : this(gateway, log, AppPaths.DailyPlaylistsFile, () => DateTimeOffset.UtcNow) { }

    public YouTubePlaylistService(
        IYouTubePlaylistGateway gateway,
        ILogService log,
        string stateFile,
        Func<DateTimeOffset> utcNow)
    {
        _gateway = gateway;
        _log = log;
        _stateFile = Path.GetFullPath(stateFile);
        _utcNow = utcNow;
    }

    public bool HasRetryableFailures
    {
        get
        {
            lock (_stateGate)
            {
                return LoadLocked().Items.Any(item => item.Status != DailyPlaylistStatus.Ready);
            }
        }
    }

    public async Task<DailyPlaylistRecord> EnsurePlaylistAsync(string room, DateOnly date, CancellationToken ct)
    {
        room = NormalizeRoom(room);
        var existing = Find(room, date);
        if (existing?.Status == DailyPlaylistStatus.Ready && !string.IsNullOrWhiteSpace(existing.PlaylistId))
        {
            return existing;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = Find(room, date);
            if (existing?.Status == DailyPlaylistStatus.Ready && !string.IsNullOrWhiteSpace(existing.PlaylistId))
            {
                return existing;
            }

            var title = $"{room} | {date:yyyy-MM-dd}";
            Save(new DailyPlaylistRecord(room, date, title, existing?.PlaylistId, existing?.Url,
                DailyPlaylistStatus.Creating, _utcNow(), null));

            var marker = Marker(room, date);
            var playlistId = await _gateway.FindPlaylistByMarkerAsync(marker, ct).ConfigureAwait(false)
                ?? await _gateway.CreatePlaylistAsync(title,
                    $"SilentStream 날짜별 자동 재생목록\n{marker}", ct).ConfigureAwait(false);
            var ready = new DailyPlaylistRecord(room, date, title, playlistId, PlaylistUrl(playlistId),
                DailyPlaylistStatus.Ready, _utcNow(), null);
            Save(ready);
            _log.Info($"날짜별 재생목록 준비 완료: {title} ({playlistId}).");
            return ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YouTubeAuthorizationRequiredException ex)
        {
            var failed = Failure(room, date, DailyPlaylistStatus.AuthRequired, ex.Message);
            Save(failed);
            return failed;
        }
        catch (TokenResponseException ex)
        {
            var failed = Failure(room, date, DailyPlaylistStatus.AuthRequired, SafeError(ex));
            Save(failed);
            return failed;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var failed = Failure(room, date, DailyPlaylistStatus.AuthRequired, SafeError(ex));
            Save(failed);
            return failed;
        }
        catch (Exception ex)
        {
            var failed = Failure(room, date, DailyPlaylistStatus.Failed, SafeError(ex));
            Save(failed);
            _log.Warn($"날짜별 재생목록 준비 실패: {room} {date:yyyy-MM-dd} - {failed.Error}");
            return failed;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AddVideoAsync(string room, DateOnly date, string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            throw new ArgumentException("YouTube video id is required.", nameof(videoId));
        }

        room = NormalizeRoom(room);
        var playlist = await EnsurePlaylistAsync(room, date, ct).ConfigureAwait(false);
        if (playlist.Status != DailyPlaylistStatus.Ready || string.IsNullOrWhiteSpace(playlist.PlaylistId))
        {
            return;
        }
        videoId = videoId.Trim();
        if (IsVideoRecorded(room, date, videoId))
        {
            return;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await _gateway.ContainsVideoAsync(playlist.PlaylistId, videoId, ct).ConfigureAwait(false))
            {
                await _gateway.AddVideoAsync(playlist.PlaylistId, videoId, ct).ConfigureAwait(false);
                _log.Info($"재생목록 영상 편입 완료: {playlist.Title} / {videoId}.");
            }
            RecordVideo(room, date, videoId);
            Save(playlist with { Status = DailyPlaylistStatus.Ready, LastAttemptAtUtc = _utcNow(), Error = null });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YouTubeAuthorizationRequiredException ex)
        {
            Save(playlist with
            {
                Status = DailyPlaylistStatus.AuthRequired,
                LastAttemptAtUtc = _utcNow(),
                Error = ex.Message
            });
        }
        catch (TokenResponseException ex)
        {
            Save(playlist with
            {
                Status = DailyPlaylistStatus.AuthRequired,
                LastAttemptAtUtc = _utcNow(),
                Error = SafeError(ex)
            });
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Save(playlist with
            {
                Status = DailyPlaylistStatus.AuthRequired,
                LastAttemptAtUtc = _utcNow(),
                Error = SafeError(ex)
            });
        }
        catch (Exception ex)
        {
            Save(playlist with
            {
                Status = DailyPlaylistStatus.Failed,
                LastAttemptAtUtc = _utcNow(),
                Error = SafeError(ex)
            });
            _log.Warn($"재생목록 영상 편입 실패: {playlist.Title} / {videoId} - {SafeError(ex)}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IReadOnlyList<DailyPlaylistRecord> Snapshot(string room, DateOnly from, DateOnly to)
    {
        room = NormalizeRoom(room);
        if (from > to)
        {
            (from, to) = (to, from);
        }
        lock (_stateGate)
        {
            return LoadLocked().Items
                .Where(item => string.Equals(item.Room, room, StringComparison.OrdinalIgnoreCase)
                    && item.Date >= from && item.Date <= to)
                .OrderByDescending(item => item.Date)
                .ToArray();
        }
    }

    private DailyPlaylistRecord? Find(string room, DateOnly date)
    {
        lock (_stateGate)
        {
            return LoadLocked().Items.FirstOrDefault(item => item.Date == date
                && string.Equals(item.Room, room, StringComparison.OrdinalIgnoreCase));
        }
    }

    private DailyPlaylistRecord Failure(string room, DateOnly date, string status, string error)
    {
        var existing = Find(room, date);
        return new DailyPlaylistRecord(room, date, $"{room} | {date:yyyy-MM-dd}",
            existing?.PlaylistId, existing?.Url, status, _utcNow(), error);
    }

    private void Save(DailyPlaylistRecord item)
    {
        lock (_stateGate)
        {
            var state = LoadLocked();
            var index = state.Items.FindIndex(candidate => candidate.Date == item.Date
                && string.Equals(candidate.Room, item.Room, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) state.Items[index] = item;
            else state.Items.Add(item);
            PersistLocked();
        }
    }

    private PlaylistState LoadLocked()
    {
        if (_state is not null) return _state;
        if (!File.Exists(_stateFile)) return _state = new PlaylistState();
        try
        {
            _state = JsonSerializer.Deserialize<PlaylistState>(File.ReadAllText(_stateFile), JsonOptions)
                ?? new PlaylistState();
            _state.Items ??= [];
            _state.AddedVideoIds ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            return _state;
        }
        catch (JsonException)
        {
            try { File.Copy(_stateFile, _stateFile + ".corrupt", true); } catch { }
            return _state = new PlaylistState();
        }
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporary, _stateFile, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string NormalizeRoom(string room) =>
        string.IsNullOrWhiteSpace(room) ? Environment.MachineName.ToLowerInvariant() : room.Trim().ToLowerInvariant();

    private static string Marker(string room, DateOnly date) =>
        $"SilentStream-Playlist-Key: {room}/{date:yyyy-MM-dd}";

    private bool IsVideoRecorded(string room, DateOnly date, string videoId)
    {
        lock (_stateGate)
        {
            return LoadLocked().AddedVideoIds.TryGetValue(Marker(room, date), out var ids)
                && ids.Contains(videoId, StringComparer.Ordinal);
        }
    }

    private void RecordVideo(string room, DateOnly date, string videoId)
    {
        lock (_stateGate)
        {
            var state = LoadLocked();
            var key = Marker(room, date);
            if (!state.AddedVideoIds.TryGetValue(key, out var ids))
            {
                state.AddedVideoIds[key] = ids = [];
            }
            if (!ids.Contains(videoId, StringComparer.Ordinal)) ids.Add(videoId);
            PersistLocked();
        }
    }

    private static string PlaylistUrl(string playlistId) =>
        $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}";

    private static string SafeError(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    private sealed class PlaylistState
    {
        public List<DailyPlaylistRecord> Items { get; set; } = [];
        public Dictionary<string, List<string>> AddedVideoIds { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
