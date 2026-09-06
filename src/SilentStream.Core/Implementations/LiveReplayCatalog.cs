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

    public void RecordStarted(LiveSession session, DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var state = LoadLocked();
            var item = new LiveReplayRecord(session.BroadcastId, session.WatchUrl, startedAtUtc, null);
            var index = state.Items.FindIndex(candidate =>
                string.Equals(candidate.BroadcastId, session.BroadcastId, StringComparison.Ordinal));
            if (index >= 0) state.Items[index] = item;
            else state.Items.Add(item);
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
            .OrderByDescending(item => item.StartedAtUtc)
            .Take(MaxRetained)
            .ToList();
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

    private sealed class State
    {
        public List<LiveReplayRecord> Items { get; set; } = [];
    }
}
