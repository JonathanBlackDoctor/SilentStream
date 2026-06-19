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

        // Process stays alive (IsRunning == true) but stops emitting progress: a stalled feed.
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
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

        public Task StopAsync()
        {
            Stopped = true;
            _running = false;
            return Task.CompletedTask;
        }

        public void SimulateDeath() => _running = false;

        public void Dispose() { }
    }

    private sealed class FakeRecording : IRecordingManager
    {
        public int RetentionRuns;
        public bool ThrowOnSweep; // throws on periodic sweeps (not the boot sweep)
        private int _files;

        public string CreateSessionFilePath(DateTime sessionStartLocal) =>
            $"/rec/SilentStream_REC_{sessionStartLocal:yyyy-MM-dd_HHmm}{(++_files > 1 ? $"_part{_files}" : "")}.mp4";

        public RecordingStatus GetStatus() => RecordingStatus.Empty;

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
