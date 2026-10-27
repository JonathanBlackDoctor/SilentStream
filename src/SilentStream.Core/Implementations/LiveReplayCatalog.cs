using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>Small durable catalogue of recent live watch URLs for the room-manager UI.</summary>
public sealed class LiveReplayCatalog : ILiveReplayCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private const int MaxRetained = 50;
    private readonly object _gate = new();
    private readonly string _stateFile;
    private State? _state;
    private readonly HashSet<string> _currentSessions = new(StringComparer.Ordinal);

    public LiveReplayCatalog() : this(AppPaths.LiveReplaysFile) { }

    public LiveReplayCatalog(string stateFile) => _stateFile = stateFile;

    public LiveReplayRecord? Latest
    {
        get
        {
            lock (_gate)
            {
                return LoadLocked().Items.MaxBy(item => item.StartedAtUtc);
            }
        }
    }

    public IReadOnlyList<LiveReplayRecord> Snapshot()
    {
        lock (_gate)
        {
            return LoadLocked().Items
                .OrderByDescending(item => item.StartedAtUtc)
                .ToArray();
        }
    }

    public void RecordStarted(LiveSession session, DateTimeOffset startedAtUtc, string? room = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var state = LoadLocked();
            var item = new LiveReplayRecord(session.BroadcastId, session.WatchUrl, startedAtUtc, null,
                room?.Trim().ToLowerInvariant());
            var index = state.Items.FindIndex(candidate =>
                string.Equals(candidate.BroadcastId, session.BroadcastId, StringComparison.Ordinal));
            if (index >= 0) state.Items[index] = item;
            else state.Items.Add(item);
            _currentSessions.Add(session.BroadcastId);
            TrimLocked(state);
            PersistLocked();
        }
    }

    public void RecordEnded(string broadcastId, DateTimeOffset endedAtUtc)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Items.FindIndex(candidate =>
                string.Equals(candidate.BroadcastId, broadcastId, StringComparison.Ordinal));
            if (index < 0) return;
            state.Items[index] = state.Items[index] with { EndedAtUtc = endedAtUtc };
            _currentSessions.Remove(broadcastId);
            PersistLocked();
        }
    }

    public bool IsCurrentSession(string broadcastId)
    {
        lock (_gate) return _currentSessions.Contains(broadcastId);
    }

    public void MarkPlaylistAdded(string broadcastId, DateTimeOffset addedAtUtc)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Items.FindIndex(item => item.BroadcastId == broadcastId);
            if (index < 0) return;
            state.Items[index] = state.Items[index] with { PlaylistAddedAtUtc = addedAtUtc };
            TrimLocked(state);
            PersistLocked();
        }
    }

    private State LoadLocked()
    {
        if (_state is not null) return _state;
        if (!File.Exists(_stateFile)) return _state = new State();
        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile), JsonOptions) ?? new State();
            _state.Items ??= [];
            TrimLocked(_state);
            return _state;
        }
        catch (JsonException)
        {
            try { File.Copy(_stateFile, _stateFile + ".corrupt", true); } catch { }
            return _state = new State();
        }
    }

    private static void TrimLocked(State state)
    {
        if (state.Items.Count <= MaxRetained) return;
        state.Items = state.Items
            .Where(item => item.PlaylistAddedAtUtc is null)
            .Concat(state.Items.Where(item => item.PlaylistAddedAtUtc is not null)
            .OrderByDescending(item => item.StartedAtUtc)
            .Take(MaxRetained))
            .ToList();
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, _state, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _stateFile, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed class State
    {
        public List<LiveReplayRecord> Items { get; set; } = [];
    }
}
