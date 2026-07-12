using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

// Fakes below implement interface events (LevelsUpdated/SamplesAvailable/MetricsUpdated) they don't all raise.
#pragma warning disable CS0067

namespace SilentStream.Tests;

/// <summary>
/// Unit tests for the health/event layer (원격 컨트롤러 개선 Phase 0). Time is driven by a mutable
/// <see cref="Clock"/> and the poll delay is parked forever, so debounce/hysteresis is fully
/// deterministic: tests raise fake subsystem events and call <see cref="HealthMonitor.RunChecksOnce"/>
/// after advancing the virtual clock.
/// </summary>
public class HealthMonitorTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-health-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeOrchestrator _orch = new();
    private readonly FakeMixer _mixer = new();
    private readonly FakeRecording _recording = new();
    private readonly FakeQueue _queue = new();
    private readonly Clock _clock = new();
    private readonly List<HealthEvent> _events = new();

    public HealthMonitorTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault()); // MinFreeGb=5, Recording.Enabled=true
    }

    private HealthMonitor CreateStarted(string? room = null)
    {
        if (room is not null)
        {
            _configStore.Update(c => c.DeviceName = room);
        }
        var hm = new HealthMonitor(
            _orch, _mixer, _recording, _queue, _configStore, new LogService(),
            () => _clock.Now,
            (_, ct) => Task.Delay(Timeout.Infinite, ct)); // park the poll loop; tests call RunChecksOnce
        hm.HealthChanged += (_, e) => { lock (_events) { _events.Add(e); } };
        hm.Start(CancellationToken.None);
        return hm;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- live_started / live_stopped ----

    [Fact]
    public void Reaching_live_emits_live_started_exactly_once_across_a_reconnect()
    {
        using var hm = CreateStarted();

        _orch.Raise(StreamState.Warmup);
        _orch.Raise(StreamState.ConnectingYouTube);
        _orch.Raise(StreamState.Live);

        var started = Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStarted);
        Assert.Equal(HealthSeverity.Info, started.Severity);

        // A watchdog reconnect (Live -> Retrying -> Live) must NOT re-announce live_started.
        _orch.Raise(StreamState.Retrying);
        _orch.Raise(StreamState.Live);
        Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStarted);
    }

    [Fact]
    public void Graceful_stop_after_live_emits_live_stopped_once()
    {
        using var hm = CreateStarted();

        _orch.Raise(StreamState.Live);
        _orch.Raise(StreamState.Stopping);
        _orch.Raise(StreamState.Idle);

        Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStopped);
    }

    [Fact]
    public void Stop_before_ever_going_live_does_not_emit_live_stopped()
    {
        using var hm = CreateStarted();

        _orch.Raise(StreamState.Warmup);
        _orch.Raise(StreamState.ConnectingYouTube);
        _orch.Raise(StreamState.Idle);

        Assert.DoesNotContain(_events, e => e.Kind == HealthEventKind.LiveStopped);
        Assert.DoesNotContain(_events, e => e.Kind == HealthEventKind.LiveStarted);
    }

    // ---- mic_silent ----

    [Fact]
    public void Mic_silence_then_recovery_emits_onset_and_clear()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, "dev1", "교탁 마이크")
        });

        _mixer.RaiseMic("mic:1", present: false);
        var onset = Assert.Single(_events, e => e.Kind == HealthEventKind.MicSilent && e.Active);
        Assert.Equal(HealthSeverity.Warn, onset.Severity);
        Assert.Equal("mic:1", onset.SourceKey);
        Assert.Contains("교탁 마이크", onset.Message);

        _mixer.RaiseMic("mic:1", present: true);
        Assert.Single(_events, e => e.Kind == HealthEventKind.MicSilent && !e.Active);
    }

    [Fact]
    public void Duplicate_silence_edges_are_not_re_emitted()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, null, "마이크1")
        });

        _mixer.RaiseMic("mic:1", present: false);
        _mixer.RaiseMic("mic:1", present: false); // repeat edge — should be ignored

        Assert.Single(_events, e => e.Kind == HealthEventKind.MicSilent && e.Active);
    }

    [Fact]
    public void Mic_silence_during_a_live_session_is_critical()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, null, "마이크1")
        });

        _orch.Raise(StreamState.Live);
        _mixer.RaiseMic("mic:1", present: false);

        var onset = Assert.Single(_events, e => e.Kind == HealthEventKind.MicSilent && e.Active);
        Assert.Equal(HealthSeverity.Critical, onset.Severity);
    }

    [Fact]
    public void Mic_silent_before_live_escalates_to_critical_when_live_starts_and_downgrades_on_stop()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, null, "마이크1")
        });

        // Silent while idle → Warn.
        _mixer.RaiseMic("mic:1", present: false);
        var idle = Assert.Single(_events, e => e.Kind == HealthEventKind.MicSilent && e.Active);
        Assert.Equal(HealthSeverity.Warn, idle.Severity);

        // Going live must escalate the still-silent mic to Critical (the dead-mic-during-broadcast case).
        _orch.Raise(StreamState.Live);
        Assert.Equal(HealthSeverity.Critical, _events.Last(e => e.Kind == HealthEventKind.MicSilent && e.Active).Severity);
        Assert.Contains(hm.ActiveEvents, e => e.Kind == HealthEventKind.MicSilent && e.Severity == HealthSeverity.Critical);

        // Stopping the session drops it back to Warn.
        _orch.Raise(StreamState.Stopping);
        Assert.Equal(HealthSeverity.Warn, _events.Last(e => e.Kind == HealthEventKind.MicSilent && e.Active).Severity);
    }

    [Fact]
    public void Broadcast_only_stop_emits_live_stopped_once_and_downgrades_mics()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, null, "마이크1")
        });

        _orch.Raise(StreamState.Live);
        _mixer.RaiseMic("mic:1", present: false); // critical while live

        // 방송만 중지 → RecordingOnly: the live session ends while recording continues.
        _orch.Raise(StreamState.RecordingOnly);

        var stopped = Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStopped);
        Assert.Contains("녹화는 계속", stopped.Message);
        Assert.Equal(HealthSeverity.Warn,
            _events.Last(e => e.Kind == HealthEventKind.MicSilent && e.Active).Severity);

        // The eventual full stop must not announce a second live_stopped.
        _orch.Raise(StreamState.Stopping);
        _orch.Raise(StreamState.Idle);
        Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStopped);
    }

    // ---- rtmp_down (hysteresis + escalation + recovery) ----

    [Fact]
    public void Rtmp_down_is_debounced_then_escalates_then_recovers()
    {
        using var hm = CreateStarted();
        _orch.Raise(StreamState.Live);
        _orch.Raise(StreamState.Retrying);

        hm.RunChecksOnce(); // 0s in Retrying — below the warn window
        Assert.DoesNotContain(_events, e => e.Kind == HealthEventKind.RtmpDown);

        _clock.Now = _clock.Now.AddSeconds(16); // past the 15s warn window
        hm.RunChecksOnce();
        var warn = Assert.Single(_events, e => e.Kind == HealthEventKind.RtmpDown && e.Active);
        Assert.Equal(HealthSeverity.Warn, warn.Severity);

        _clock.Now = _clock.Now.AddSeconds(50); // ~66s total — past the 60s critical window
        hm.RunChecksOnce();
        Assert.Contains(_events, e =>
            e.Kind == HealthEventKind.RtmpDown && e.Active && e.Severity == HealthSeverity.Critical);

        _orch.Raise(StreamState.Live); // recovery clears the condition
        Assert.Contains(_events, e => e.Kind == HealthEventKind.RtmpDown && !e.Active);
        Assert.DoesNotContain(hm.ActiveEvents, e => e.Kind == HealthEventKind.RtmpDown);
    }

    [Fact]
    public void Brief_retry_that_recovers_before_the_window_never_alarms()
    {
        using var hm = CreateStarted();
        _orch.Raise(StreamState.Live);
        _orch.Raise(StreamState.Retrying);

        _clock.Now = _clock.Now.AddSeconds(5); // still inside the debounce window
        hm.RunChecksOnce();
        _orch.Raise(StreamState.Live); // reconnected quickly

        Assert.DoesNotContain(_events, e => e.Kind == HealthEventKind.RtmpDown);
    }

    // ---- disk_low (hysteresis + escalation) ----

    [Fact]
    public void Disk_low_trips_warn_holds_within_hysteresis_then_clears_above_margin()
    {
        using var hm = CreateStarted();

        _recording.FreeBytes = 3 * GB; // below 5 GB threshold
        hm.RunChecksOnce();
        var warn = Assert.Single(_events, e => e.Kind == HealthEventKind.DiskLow && e.Active);
        Assert.Equal(HealthSeverity.Warn, warn.Severity);

        _recording.FreeBytes = 5 * GB + 512 * 1024 * 1024; // 5.5 GB: above threshold but within clear margin
        hm.RunChecksOnce();
        Assert.Single(_events, e => e.Kind == HealthEventKind.DiskLow); // no new event — still tripped

        _recording.FreeBytes = 7 * GB; // above threshold + 1 GB margin
        hm.RunChecksOnce();
        Assert.Single(_events, e => e.Kind == HealthEventKind.DiskLow && !e.Active);
    }

    [Fact]
    public void Disk_low_escalates_warn_to_critical()
    {
        using var hm = CreateStarted();

        _recording.FreeBytes = 3 * GB; // warn
        hm.RunChecksOnce();
        _recording.FreeBytes = 1 * GB; // below half the threshold (2.5 GB) -> critical
        hm.RunChecksOnce();

        Assert.Contains(_events, e =>
            e.Kind == HealthEventKind.DiskLow && e.Active && e.Severity == HealthSeverity.Critical);
    }

    [Fact]
    public void Disk_low_de_escalates_critical_to_warn_when_space_recovers_into_the_warn_band()
    {
        using var hm = CreateStarted();

        _recording.FreeBytes = 1 * GB; // < 2.5 GB -> critical
        hm.RunChecksOnce();
        Assert.Equal(HealthSeverity.Critical, _events.Last(e => e.Kind == HealthEventKind.DiskLow && e.Active).Severity);

        // Recover to 4 GB: above critical+margin (3.5 GB) but still below the 5 GB threshold -> should downgrade to Warn.
        _recording.FreeBytes = 4 * GB;
        hm.RunChecksOnce();
        Assert.Equal(HealthSeverity.Warn, _events.Last(e => e.Kind == HealthEventKind.DiskLow && e.Active).Severity);
        Assert.Contains(hm.ActiveEvents, e => e.Kind == HealthEventKind.DiskLow && e.Severity == HealthSeverity.Warn);
        Assert.DoesNotContain(hm.ActiveEvents, e => e.Kind == HealthEventKind.DiskLow && e.Severity == HealthSeverity.Critical);
    }

    // ---- upload_failed ----

    [Fact]
    public void Upload_failure_is_reported_once_per_job()
    {
        using var hm = CreateStarted();
        _queue.Jobs.Add(Job("j1", "1교시 - 2026-06-14", UploadJobStatus.Failed));

        hm.RunChecksOnce();
        var failed = Assert.Single(_events, e => e.Kind == HealthEventKind.UploadFailed);
        Assert.Equal("j1", failed.SourceKey);
        Assert.Contains("1교시", failed.Message);

        hm.RunChecksOnce(); // same failed job — must not re-report
        Assert.Single(_events, e => e.Kind == HealthEventKind.UploadFailed);

        _queue.Jobs.Add(Job("j2", "2교시 - 2026-06-14", UploadJobStatus.Failed));
        hm.RunChecksOnce();
        Assert.Equal(2, _events.Count(e => e.Kind == HealthEventKind.UploadFailed));
    }

    [Fact]
    public void Pending_or_uploading_jobs_are_not_reported_as_failed()
    {
        using var hm = CreateStarted();
        _queue.Jobs.Add(Job("j1", "1교시", UploadJobStatus.Pending));
        _queue.Jobs.Add(Job("j2", "2교시", UploadJobStatus.Uploading));

        hm.RunChecksOnce();

        Assert.DoesNotContain(_events, e => e.Kind == HealthEventKind.UploadFailed);
    }

    // ---- ActiveEvents + room stamp ----

    [Fact]
    public void Active_events_tracks_conditions_but_not_momentary_notifications()
    {
        using var hm = CreateStarted();
        _mixer.ConfigureSources(new[]
        {
            new AudioSourceSettings("mic:1", AudioSourceKind.Microphone, null, "마이크1")
        });

        _mixer.RaiseMic("mic:1", present: false);
        _recording.FreeBytes = 1 * GB;
        hm.RunChecksOnce();
        _orch.Raise(StreamState.Live);

        Assert.Contains(hm.ActiveEvents, e => e.Kind == HealthEventKind.MicSilent);
        Assert.Contains(hm.ActiveEvents, e => e.Kind == HealthEventKind.DiskLow);
        Assert.DoesNotContain(hm.ActiveEvents, e => e.Kind == HealthEventKind.LiveStarted);

        _mixer.RaiseMic("mic:1", present: true); // recovery removes it from the active set
        Assert.DoesNotContain(hm.ActiveEvents, e => e.Kind == HealthEventKind.MicSilent);
    }

    [Fact]
    public void Events_are_stamped_with_the_room_label()
    {
        using var hm = CreateStarted(room: "201호");

        _orch.Raise(StreamState.Live);

        var started = Assert.Single(_events, e => e.Kind == HealthEventKind.LiveStarted);
        Assert.Equal("201호", started.Room);
    }

    [Fact]
    public void Room_label_is_refreshed_at_runtime_not_frozen_at_start()
    {
        using var hm = CreateStarted(room: "PC-A");

        _orch.Raise(StreamState.Live);
        Assert.Equal("PC-A", _events.Last(e => e.Kind == HealthEventKind.LiveStarted).Room);

        // Operator renames the room mid-session; the next poll refreshes the stamp.
        _configStore.Update(c => c.DeviceName = "201호");
        hm.RunChecksOnce();

        _orch.Raise(StreamState.Stopping);
        Assert.Equal("201호", _events.Last(e => e.Kind == HealthEventKind.LiveStopped).Room);
    }

    private static UploadJob Job(string id, string title, string status) =>
        new(id, id + ".mp4", title, new DateOnly(2026, 6, 14), 1, status, 5, null);

    // ---- fakes ----

    private sealed class Clock
    {
        public DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeOrchestrator : IStreamOrchestrator
    {
        public StreamState State { get; private set; } = StreamState.Idle;
        public event EventHandler<StreamState>? StateChanged;
        public event EventHandler<MetricsSnapshot>? MetricsUpdated;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task StopStreamingKeepRecordingAsync() => Task.CompletedTask;
        public void Raise(StreamState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private sealed class FakeMixer : IAudioMixer
    {
        private readonly List<AudioSourceSettings> _sources = new();
        public IReadOnlyList<AudioSourceSettings> Sources => _sources;
        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => Array.Empty<AudioDeviceInfo>();
        public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources)
        {
            _sources.Clear();
            _sources.AddRange(sources);
        }
        public void SetGain(string sourceId, double gain) { }
        public void SetMuted(string sourceId, bool muted) { }
        public AudioLevels CurrentLevels => AudioLevels.Empty;
        public event EventHandler<AudioLevels>? LevelsUpdated;
        public event EventHandler<MicSignalStatus>? MicSignalChanged;
        public event EventHandler<AudioBuffer>? SamplesAvailable;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
        public void RaiseMic(string sourceId, bool present) =>
            MicSignalChanged?.Invoke(this, new MicSignalStatus(sourceId, present, DateTime.UtcNow));
    }

    private sealed class FakeRecording : IRecordingManager
    {
        public long FreeBytes = 500L * 1024 * 1024 * 1024; // 500 GB healthy default
        public string CreateSessionFilePath(DateTime sessionStartLocal) => "session.mp4";
        public RecordingStatus GetStatus() => new(null, 0, FreeBytes);
        public long GetActiveRecordingLength() => 0;
        public Task EnforceRetentionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeQueue : IUploadQueue
    {
        public List<UploadJob> Jobs { get; } = new();
        public void Enqueue(UploadJob job) => Jobs.Add(job);
        public IReadOnlyList<UploadJob> Snapshot() => Jobs.ToList();
        public void Start(CancellationToken ct) { }
    }
}
