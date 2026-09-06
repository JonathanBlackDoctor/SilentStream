using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class LiveReplayCatalogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-replays-").FullName;

    [Fact]
    public void Started_and_ended_replay_survives_app_restart_without_ingest_secrets()
    {
        var stateFile = Path.Combine(_directory, "live_replays.json");
        var startedAt = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var endedAt = startedAt.AddHours(10);
        var catalog = new LiveReplayCatalog(stateFile);
        catalog.RecordStarted(new LiveSession("broadcast-1", "secret-stream-key",
            "rtmp://ingest.example/live2", "https://youtu.be/broadcast-1"), startedAt);
        catalog.RecordEnded("broadcast-1", endedAt);

        var restarted = new LiveReplayCatalog(stateFile);
        var replay = Assert.IsType<LiveReplayRecord>(restarted.Latest);
        Assert.Equal("broadcast-1", replay.BroadcastId);
        Assert.Equal("https://youtu.be/broadcast-1", replay.Url);
        Assert.Equal(startedAt, replay.StartedAtUtc);
        Assert.Equal(endedAt, replay.EndedAtUtc);
        Assert.DoesNotContain("secret-stream-key", File.ReadAllText(stateFile), StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp://", File.ReadAllText(stateFile), StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
