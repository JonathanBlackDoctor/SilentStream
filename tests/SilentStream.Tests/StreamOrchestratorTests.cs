using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class StreamOrchestratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-orch-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeYouTube _youtube = new();
    private readonly FakeEncoder _encoder = new();
    private readonly FakeRecording _recording = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeMixer _mixer = new();
    private readonly List<StreamState> _states = [];

    public StreamOrchestratorTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Recording.Folder = _dir;
        _configStore.Save(config);
    }

    private StreamOrchestrator CreateOrchestrator(TimeSpan? watchdog = null, TimeSpan? retention = null)
    {
        var orchestrator = new StreamOrchestrator(
            _configStore, new LogService(), _youtube, _encoder, _recording, _capture, _mixer,
            new StreamOrchestratorOptions
            {
                WarmupDelay = TimeSpan.FromMilliseconds(10),
                RetryBaseDelay = TimeSpan.FromMilliseconds(5),
                RetryMaxDelay = TimeSpan.FromMilliseconds(40),
                WatchdogInterval = watchdog ?? TimeSpan.FromMilliseconds(25),
                StallTimeout = TimeSpan.FromMilliseconds(200),
                RetentionInterval = retention ?? TimeSpan.FromHours(1)
            });
        orchestrator.StateChanged += (_, s) => { lock (_states) { _states.Add(s); } };
        return orchestrator;
    }

    [Fact]
    public async Task Happy_path_walks_idle_warmup_connecting_live()
    {
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(StreamState.Live, orchestrator.State);
        lock (_states)
        {
            Assert.Equal(
                [StreamState.Warmup, StreamState.ConnectingYouTube, StreamState.Live],
                _states);
        }
        Assert.Equal(1, _recording.RetentionRuns); // boot-time sweep (plan §4.2-4)
        Assert.True(_capture.Started);
        Assert.True(_mixer.Started);

        var options = Assert.Single(_encoder.StartCalls);
        Assert.Equal("rtmp://ingest.example/live2/key-1", options.RtmpUrl);
        Assert.EndsWith(".mp4", options.RecordingFilePath);
        Assert.Equal(1920, options.Width);
        Assert.Equal(9000, options.VideoBitrateKbps); // 1080p@60 → 9000kbps
    }

    [Fact]
    public async Task Failed_connect_brings_up_recording_only_then_switches_to_tee_on_live()
    {
        _youtube.FailuresBeforeSuccess = 3;
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(StreamState.Live, orchestrator.State);
        lock (_states)
        {
            Assert.Equal(3, _states.Count(s => s == StreamState.Retrying));
        }
        Assert.Equal(4, _youtube.CreateCalls);

        // §3.6/§4.1: after the first failed connect the encoder comes up recording-only
        // (no RTMP), then switches to the RTMP+mp4 tee once streaming finally goes live.
        Assert.Equal(2, _encoder.StartCalls.Count);
        Assert.Equal(string.Empty, _encoder.StartCalls[0].RtmpUrl);
        Assert.EndsWith(".mp4", _encoder.StartCalls[0].RecordingFilePath);
        Assert.Equal("rtmp://ingest.example/live2/key-4", _encoder.StartCalls[1].RtmpUrl);
        Assert.EndsWith(".mp4", _encoder.StartCalls[1].RecordingFilePath);
    }

    [Fact]
    public async Task Recording_starts_even_when_youtube_never_connects()
    {
        _youtube.FailuresBeforeSuccess = int.MaxValue; // streaming never succeeds
        var orchestrator = CreateOrchestrator();
        using var cts = new CancellationTokenSource();

        // StartAsync never returns while connecting loops forever, so don't await it.
        var startTask = orchestrator.StartAsync(cts.Token);
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 1, TimeSpan.FromSeconds(5));

        // The local backup recording is running despite streaming being stuck retrying.
        var recordingOnly = Assert.Single(_encoder.StartCalls);
        Assert.Equal(string.Empty, recordingOnly.RtmpUrl);          // recording-only: no RTMP
        Assert.EndsWith(".mp4", recordingOnly.RecordingFilePath);   // ...but writing a file
        Assert.True(_encoder.IsRunning);
        Assert.NotEqual(StreamState.Live, orchestrator.State);

        cts.Cancel();
        await startTask; // StartAsync swallows the cancellation and stops cleanly
        Assert.True(_encoder.Stopped);
    }

    [Fact]
    public async Task Stop_finalises_encoder_then_completes_broadcast_then_idles()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        await orchestrator.StopAsync();

        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.True(_encoder.Stopped);
        Assert.Equal("bc-1", _youtube.CompletedBroadcastId);
        lock (_states)
        {
            Assert.Equal(StreamState.Stopping, _states[^2]);
            Assert.Equal(StreamState.Idle, _states[^1]);
        }
    }

    [Fact]
    public async Task Stop_broadcast_keeps_recording_and_completes_the_broadcast()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        await orchestrator.StopStreamingKeepRecordingAsync();

        // The tee encoder was swapped for a recording-only one: same session, new part file.
        Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
        Assert.Equal(2, _encoder.StartCalls.Count);
        Assert.Equal(string.Empty, _encoder.StartCalls[1].RtmpUrl);
        Assert.EndsWith(".mp4", _encoder.StartCalls[1].RecordingFilePath);
        Assert.True(_encoder.IsRunning);
        // Capture/audio keep feeding the recording; the broadcast is completed (no ghost live).
        Assert.True(_capture.Started);
        Assert.True(_mixer.Started);
        Assert.Equal("bc-1", _youtube.CompletedBroadcastId);
        Assert.Equal(1, _youtube.CompleteCalls);

        // A later full stop finalises the recording too — without re-completing the broadcast.
        await orchestrator.StopAsync();
        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Equal(1, _youtube.CompleteCalls);
    }

    [Fact]
    public async Task Stop_broadcast_from_non_live_is_a_no_op()
    {
        var orchestrator = CreateOrchestrator();

        await orchestrator.StopStreamingKeepRecordingAsync(); // Idle — nothing to drop

        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Empty(_encoder.StartCalls);
        Assert.Equal(0, _youtube.CompleteCalls);
    }

    [Fact]
    public async Task Stop_broadcast_with_recording_disabled_degenerates_to_a_full_stop()
    {
        var config = _configStore.Load();
        config.Recording.Enabled = false;
        _configStore.Save(config);
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        await orchestrator.StopStreamingKeepRecordingAsync();

        // No recording leg to keep → full stop semantics.
        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.True(_encoder.Stopped);
        Assert.Single(_encoder.StartCalls); // no recording-only restart
        Assert.Equal("bc-1", _youtube.CompletedBroadcastId);
    }

    [Fact]
    public async Task Full_stop_during_broadcast_swap_leaves_no_orphan_encoder()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // Hold the swap inside its encoder stop (ffmpeg flush can take ~10s in the field)…
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _encoder.StopGate = gate;
        var swap = orchestrator.StopStreamingKeepRecordingAsync();
        await WaitUntilAsync(() => orchestrator.State == StreamState.RecordingOnly, TimeSpan.FromSeconds(5));

        // …while a full stop races through its entire teardown to Idle.
        var fullStop = orchestrator.StopAsync();
        await WaitUntilAsync(() => orchestrator.State == StreamState.Idle, TimeSpan.FromSeconds(5));

        gate.SetResult(); // release the swap: it now starts a fresh recording encoder post-teardown
        await swap;
        await fullStop;

        // The swap must notice the finished teardown and stop its fresh encoder — no orphan ffmpeg
        // left running while every UI shows Idle.
        Assert.False(_encoder.IsRunning);
        Assert.Equal(StreamState.Idle, orchestrator.State);
    }

    [Fact]
    public async Task Quality_swap_keeps_live_state_and_broadcast_and_opens_a_new_part_file()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        QualityStatus? published = null;
        orchestrator.QualityChanged += (_, q) => published = q;

        var target = QualityStatus.Original with
        {
            Level = 1,
            LevelName = "절약 1단계",
            Reason = QualityChangeReason.ManualSet
        };
        Assert.True(await orchestrator.SwapLiveEncoderAsync(target));

        // Same broadcast + ingest (no orphan, no new watch URL), state stays Live throughout,
        // and the recording continues in a NEW part file (frag mp4 cannot be appended).
        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.Equal(2, _encoder.StartCalls.Count);
        Assert.Equal("rtmp://ingest.example/live2/key-1", _encoder.StartCalls[1].RtmpUrl);
        Assert.Contains("_part", _encoder.StartCalls[1].RecordingFilePath);
        Assert.True(_encoder.IsRunning);
        Assert.Equal(1, _youtube.CreateCalls);
        Assert.Equal(0, _youtube.CompleteCalls);
        Assert.Equal(target, orchestrator.CurrentQuality);
        Assert.Equal(target, published);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Quality_swap_from_non_live_is_rejected()
    {
        var orchestrator = CreateOrchestrator();

        Assert.False(await orchestrator.SwapLiveEncoderAsync(QualityStatus.Original));

        Assert.Empty(_encoder.StartCalls);
        Assert.Equal(QualityStatus.Original, orchestrator.CurrentQuality);
    }

    [Fact]
    public async Task Watchdog_leaves_an_in_flight_quality_swap_alone()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // Hold the swap inside its encoder stop: IsRunning is false while the state is still Live —
        // exactly the window the watchdog would misread as a dead encoder and double-start from.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _encoder.StopGate = gate;
        var swap = orchestrator.SwapLiveEncoderAsync(
            QualityStatus.Original with { Level = 1, LevelName = "절약 1단계" });

        await Task.Delay(200); // many 25ms watchdog ticks over the held stop window
        Assert.Single(_encoder.StartCalls);                 // no watchdog double-start
        Assert.Equal(StreamState.Live, orchestrator.State); // and no reconnect claim

        gate.SetResult();
        Assert.True(await swap);
        Assert.Equal(2, _encoder.StartCalls.Count); // exactly the swap's own restart
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Full_stop_during_quality_swap_leaves_no_orphan_encoder()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // Hold the quality swap inside its encoder stop…
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _encoder.StopGate = gate;
        var swap = orchestrator.SwapLiveEncoderAsync(
            QualityStatus.Original with { Level = 1, LevelName = "절약 1단계" });

        // …while a full stop races through its entire teardown to Idle.
        var fullStop = orchestrator.StopAsync();
        await WaitUntilAsync(() => orchestrator.State == StreamState.Idle, TimeSpan.FromSeconds(5));

        gate.SetResult(); // release the swap: it now starts a fresh encoder post-teardown
        Assert.False(await swap); // …and must notice the teardown and re-stop it
        await fullStop;

        Assert.False(_encoder.IsRunning);
        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Equal(QualityStatus.Original, orchestrator.CurrentQuality); // no phantom publication
    }

    [Fact]
    public async Task Watchdog_treats_a_shrunken_recording_file_as_a_fresh_part_file_not_a_stall()
    {
        _recording.ActiveRecordingLength = 50_000_000; // a long-running part file
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        // Let the growth stamp age past StallTimeout while the file size stays constant.
        await Task.Delay(300);

        // A new (small) part file replaces the active one while the progress line is quiet —
        // exactly the post-swap window. The shrink must re-baseline the growth signal instead of
        // being compared against the old length (which would trip a false stall restart).
        _recording.ActiveRecordingLength = 1_000_000;
        _recording.GrowOnSample = true;                       // …and it keeps growing normally
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // progress line still quiet

        await Task.Delay(300); // many watchdog ticks
        Assert.Single(_encoder.StartCalls); // no false restart
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_revives_a_dead_recording_only_encoder_without_reconnecting()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        await orchestrator.StopStreamingKeepRecordingAsync();
        Assert.Equal(2, _encoder.StartCalls.Count);

        _encoder.SimulateDeath();
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 3, TimeSpan.FromSeconds(5));

        // Recording is revived recording-only; the stream must NOT be re-established.
        Assert.Equal(string.Empty, _encoder.StartCalls[^1].RtmpUrl);
        Assert.EndsWith(".mp4", _encoder.StartCalls[^1].RecordingFilePath);
        Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
        Assert.Equal(1, _youtube.CreateCalls); // no new broadcast
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restarts_a_dead_encoder()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        _encoder.SimulateDeath();
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(_encoder.StartCalls.Count >= 2);
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restarts_a_stalled_encoder_even_while_process_is_alive()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        // Process stays alive (IsRunning == true) but stops emitting progress AND the recording
        // file is not growing (FakeRecording default length stays 0): a genuinely stalled feed.
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(_encoder.StartCalls.Count >= 2);
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_leaves_a_quiet_but_still_recording_encoder_alone()
    {
        // C2 hardening: a static screen + silent audio starve ffmpeg's progress line, but the
        // encoder keeps muxing frames to the local file. A growing recording file must veto the
        // stall restart so a healthy pipeline is not churned into a ~30s restart loop.
        _recording.GrowOnSample = true;
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // progress line quiet…
        await Task.Delay(300); // …for many watchdog intervals (25ms each)

        Assert.True(_recording.ActiveRecordingLength > 0); // the watchdog actually sampled the file
        Assert.Single(_encoder.StartCalls); // …and recording is advancing, so no restart
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_with_recording_disabled_restarts_a_stalled_feed_on_the_stats_signal_alone()
    {
        // Recording-off sessions have no file (GetActiveRecordingLength stays 0), so the stall
        // restart must fall back to the ffmpeg progress line alone — the original behaviour. Pinned
        // against the actual Recording.Enabled=false flag, not just the fake's default length.
        var config = _configStore.Load();
        config.Recording.Enabled = false;
        _configStore.Save(config);
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // no file signal → stats decide
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(_encoder.StartCalls.Count >= 2);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restarts_when_both_the_feed_and_the_recording_file_freeze()
    {
        // A real hang: the encoder was recording fine, then both signals die — no ffmpeg progress
        // AND the file stops growing. With no live signal left, the watchdog must rebuild.
        _recording.GrowOnSample = true;
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(_encoder.StartCalls);

        _recording.GrowOnSample = false; // recording file frozen
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // and the feed is quiet
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(_encoder.StartCalls.Count >= 2);
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_survives_a_retention_sweep_fault_and_keeps_running()
    {
        // A throwing periodic sweep used to kill the supervisor task silently (→ ~2h idle in the
        // field). The hardened loop must log and keep watching.
        _recording.ThrowOnSweep = true;
        var orchestrator = CreateOrchestrator(retention: TimeSpan.FromMilliseconds(20));

        await orchestrator.StartAsync(CancellationToken.None);
        await Task.Delay(150); // several sweep intervals, at least one of which throws

        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.True(_recording.RetentionRuns >= 2); // boot sweep + ≥1 periodic (throwing) sweep
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restart_reuses_the_same_broadcast_no_orphans()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Equal(1, _youtube.CreateCalls);

        _encoder.SimulateDeath();
        await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2, TimeSpan.FromSeconds(5));

        // The restart must reuse the existing broadcast, not spawn a new (orphan) one, and must
        // not complete it mid-session — that was the field-test "7 orphan broadcasts" bug.
        Assert.Equal(1, _youtube.CreateCalls);
        Assert.Null(_youtube.CompletedBroadcastId);

        await orchestrator.StopAsync();
        Assert.Equal("bc-1", _youtube.CompletedBroadcastId); // completed only at shutdown
    }

    [Fact]
    public async Task Disabled_recording_streams_without_a_file()
    {
        var config = _configStore.Load();
        config.Recording.Enabled = false;
        _configStore.Save(config);
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(_encoder.StartCalls).RecordingFilePath);
    }

    [Fact]
    public async Task Audio_failure_does_not_block_going_live()
    {
        _mixer.FailOnStart = true;
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(StreamState.Live, orchestrator.State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- fakes ----

    private sealed class FakeYouTube : IYouTubeLiveService
    {
        public int FailuresBeforeSuccess;
        public int CreateCalls;
        public int CompleteCalls;
        public string? CompletedBroadcastId;

        public Task<bool> AuthenticateAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<LiveSession> CreateBroadcastAsync(CancellationToken ct)
        {
            CreateCalls++;
            if (FailuresBeforeSuccess-- > 0)
            {
                throw new HttpRequestException("simulated network failure");
            }
            return Task.FromResult(new LiveSession(
                $"bc-{CreateCalls}", $"key-{CreateCalls}",
                "rtmp://ingest.example/live2", "https://youtu.be/x"));
        }

        public Task CompleteBroadcastAsync(string broadcastId, CancellationToken ct)
        {
            CompleteCalls++;
            CompletedBroadcastId = broadcastId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEncoder : IEncoderPipeline
    {
        public List<EncoderStartOptions> StartCalls { get; } = [];
        public bool Stopped;
        private bool _running;

        public bool IsRunning => _running;

        /// <summary>Settable so tests can simulate a stalled feed (process alive, no progress).</summary>
        public TimeSpan TimeSinceProgress { get; set; } = TimeSpan.Zero;

#pragma warning disable CS0067
        public event EventHandler<MetricsSnapshot>? MetricsUpdated;
        public event EventHandler? UnexpectedExit;
#pragma warning restore CS0067

        public Task StartAsync(EncoderStartOptions options, CancellationToken ct)
        {
            StartCalls.Add(options);
            _running = true;
            TimeSinceProgress = TimeSpan.Zero; // a fresh start clears any prior stall
            return Task.CompletedTask;
        }

        /// <summary>When set, the NEXT StopAsync awaits it — simulates ffmpeg's slow (~10s) flush.</summary>
        public TaskCompletionSource? StopGate;

        public async Task StopAsync()
        {
            Stopped = true;
            _running = false;
            var gate = StopGate;
            StopGate = null;
            if (gate is not null)
            {
                await gate.Task;
            }
        }

        public void SimulateDeath() => _running = false;

        public void Dispose() { }
    }

    private sealed class FakeRecording : IRecordingManager
    {
        public int RetentionRuns;
        public bool ThrowOnSweep; // throws on periodic sweeps (not the boot sweep)
        public long ActiveRecordingLength; // current recording-file size the watchdog samples
        public bool GrowOnSample; // simulate ffmpeg writing: grows on each sample (keyed to sampling, not wall-clock)
        private int _files;

        public string CreateSessionFilePath(DateTime sessionStartLocal) =>
            $"/rec/SilentStream_REC_{sessionStartLocal:yyyy-MM-dd_HHmm}{(++_files > 1 ? $"_part{_files}" : "")}.mp4";

        public RecordingStatus GetStatus() => RecordingStatus.Empty;

        public long GetActiveRecordingLength()
        {
            if (GrowOnSample)
            {
                ActiveRecordingLength += 1_000_000;
            }
            return ActiveRecordingLength;
        }

        public Task EnforceRetentionAsync(CancellationToken ct)
        {
            RetentionRuns++;
            if (ThrowOnSweep && RetentionRuns >= 2)
            {
                throw new IOException("simulated retention sweep failure");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public bool Started;
        public bool IsCapturing => Started;
        public int Width => 1920;
        public int Height => 1080;
        public double Fps => 60;

        public IReadOnlyList<MonitorInfo> GetMonitors() => [];

#pragma warning disable CS0067
        public event EventHandler<VideoFrame>? FrameCaptured;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Started = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeMixer : IAudioMixer
    {
        public bool Started;
        public bool FailOnStart;

        public IReadOnlyList<AudioSourceSettings> Sources { get; private set; } = [];
        public AudioLevels CurrentLevels => AudioLevels.Empty;

#pragma warning disable CS0067
        public event EventHandler<AudioLevels>? LevelsUpdated;
        public event EventHandler<MicSignalStatus>? MicSignalChanged;
        public event EventHandler<AudioBuffer>? SamplesAvailable;
#pragma warning restore CS0067

        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => [];

        public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources) => Sources = sources;

        public void SetGain(string sourceId, double gain) { }

        public void SetMuted(string sourceId, bool muted) { }

        public Task StartAsync(CancellationToken ct)
        {
            if (FailOnStart)
            {
                throw new InvalidOperationException("simulated audio failure");
            }
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Started = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
