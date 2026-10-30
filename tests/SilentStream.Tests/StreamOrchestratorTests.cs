using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class StreamOrchestratorTests : IDisposable, IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-orch-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeYouTube _youtube = new();
    private readonly FakeEncoder _encoder = new();
    private readonly FakeRecording _recording = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeMixer _mixer = new();
    private readonly FakeQuality _quality = new();
    private readonly List<StreamState> _states = [];
    private readonly List<StreamOrchestrator> _createdOrchestrators = [];

    public StreamOrchestratorTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Recording.Folder = _dir;
        _configStore.Save(config);
    }

    private StreamOrchestrator CreateOrchestrator(
        TimeSpan? watchdog = null,
        TimeSpan? retention = null,
        TimeSpan? liveAutoStop = null,
        TimeSpan? liveAutoStopRetry = null,
        ILiveReplayCatalog? liveReplays = null,
        TimeSpan? broadcastCompletionTimeout = null,
        ILogService? log = null)
    {
        var orchestrator = new StreamOrchestrator(
            _configStore, log ?? new LogService(), _youtube, _encoder, _recording, _capture, _mixer, _quality,
            new StreamOrchestratorOptions
            {
                WarmupDelay = TimeSpan.FromMilliseconds(10),
                RetryBaseDelay = TimeSpan.FromMilliseconds(5),
                RetryMaxDelay = TimeSpan.FromMilliseconds(40),
                WatchdogInterval = watchdog ?? TimeSpan.FromMilliseconds(25),
                StallTimeout = TimeSpan.FromMilliseconds(200),
                RetentionInterval = retention ?? TimeSpan.FromHours(1),
                RtmpLegRetryBaseDelay = TimeSpan.FromMilliseconds(100),
                RtmpLegRetryMaxDelay = TimeSpan.FromMilliseconds(400),
                LiveAutoStopAfter = liveAutoStop ?? TimeSpan.FromHours(10),
                LiveAutoStopRetryAfter = liveAutoStopRetry ?? TimeSpan.FromHours(11),
                BroadcastCompletionTimeout = broadcastCompletionTimeout ?? TimeSpan.FromSeconds(10)
            }, liveReplays);
        orchestrator.StateChanged += (_, s) => { lock (_states) { _states.Add(s); } };
        _createdOrchestrators.Add(orchestrator);
        return orchestrator;
    }

    // Existing scenarios count transitions after the now-required initial standalone backup.
    // Fallback_* tests explicitly verify all starts, including that first recording part.
    private IReadOnlyList<EncoderStartOptions> StartsAfterInitialBackup => _encoder.StartCalls
        .Skip(_configStore.Load().Recording.Enabled ? 1 : 0).ToArray();

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

        var options = Assert.Single(StartsAfterInitialBackup);
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
    public async Task Force_retry_skips_a_pending_backoff_interval()
    {
        _youtube.FailuresBeforeSuccess = 1;
        var orchestrator = new StreamOrchestrator(
            _configStore, new LogService(), _youtube, _encoder, _recording, _capture, _mixer, _quality,
            new StreamOrchestratorOptions
            {
                WarmupDelay = TimeSpan.Zero,
                RetryBaseDelay = TimeSpan.FromSeconds(10),
                RetryMaxDelay = TimeSpan.FromSeconds(10),
                WatchdogInterval = TimeSpan.FromSeconds(10),
                StallTimeout = TimeSpan.FromMinutes(1),
                RetentionInterval = TimeSpan.FromHours(1)
            });

        var start = orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => orchestrator.State == StreamState.Retrying, TimeSpan.FromSeconds(2));

        Assert.True(await orchestrator.ForceRetryAsync());
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.Equal(2, _youtube.CreateCalls);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Force_retry_rebuilds_a_live_encoder_without_creating_a_broadcast()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        Assert.True(await orchestrator.ForceRetryAsync());

        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.Equal(2, StartsAfterInitialBackup.Count);
        Assert.Equal(1, _youtube.CreateCalls);
        await orchestrator.StopAsync();
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
    public async Task Fallback_normal_boot_records_before_the_live_tee_in_distinct_parts()
    {
        var orchestrator = CreateOrchestrator();
        try
        {
            await orchestrator.StartAsync(CancellationToken.None);
            Assert.Equal(2, _encoder.StartCalls.Count);
            Assert.Equal(string.Empty, _encoder.StartCalls[0].RtmpUrl);
            Assert.NotEmpty(_encoder.StartCalls[1].RtmpUrl);
            Assert.NotEqual(_encoder.StartCalls[0].RecordingFilePath, _encoder.StartCalls[1].RecordingFilePath);
            Assert.Equal(StreamState.Live, orchestrator.State);
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task Fallback_resume_does_not_hold_the_encoder_lock_during_authentication()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        await orchestrator.StopStreamingKeepRecordingAsync();
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var calls = _youtube.AuthenticateCalls;
        var starts = _encoder.StartCalls.Count;
        var resume = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == calls + 1, TimeSpan.FromSeconds(2));
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            _encoder.SimulateDeath();
            await WaitUntilAsync(() => _encoder.StartCalls.Count > starts && _encoder.IsRunning, TimeSpan.FromSeconds(2));
            Assert.True(_encoder.IsRunning);
            Assert.True(_encoder.StartCalls.Count > starts);
            Assert.Equal(calls + 1, _youtube.AuthenticateCalls);
            Assert.Equal(string.Empty, _encoder.StartCalls[^1].RtmpUrl);
            auth.TrySetResult(true);
            await resume.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
        }
        finally { await orchestrator.StopAsync(); await resume.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Fallback_late_authentication_result_does_not_create_a_stale_broadcast()
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => _youtube.AuthenticateCalls == 1 ? auth.Task : Task.FromResult(true);
        var orchestrator = CreateOrchestrator();
        var oldStart = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var starts = _encoder.StartCalls.Count;
            auth.TrySetResult(true);
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, _youtube.CreateCalls);
            Assert.Equal(starts, _encoder.StartCalls.Count);
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_encoder.IsRunning);
        }
        finally { auth.TrySetResult(true); await oldStart.WaitAsync(TimeSpan.FromSeconds(2)); await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task Fallback_stop_during_guard_recovery_does_not_affect_the_next_run()
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var recordingStarts = 0;
        _encoder.StartOverride = async (options, token) =>
        {
            if (options.RtmpUrl.Length == 0 && Interlocked.Increment(ref recordingStarts) == 2)
            { recovering.TrySetResult(); await release.Task.WaitAsync(token); }
        };
        var orchestrator = CreateOrchestrator();
        var oldStart = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            _encoder.SimulateDeath();
            await recovering.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            _youtube.AuthenticateOverride = null;
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var starts = _encoder.StartCalls.Count;
            release.TrySetResult();
            Assert.Equal(starts, _encoder.StartCalls.Count);
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_encoder.IsRunning);
            Assert.Equal(1, _encoder.MaxConcurrentOperations);
        }
        finally { release.TrySetResult(); await orchestrator.StopAsync(); await oldStart.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Fallback_stop_is_not_blocked_by_a_reconnect_api_ignoring_cancellation()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task; // a provider does not honor cancellation
        var calls = _youtube.AuthenticateCalls;
        _encoder.SimulateDeath();
        Task? stopping = null;
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls > calls, TimeSpan.FromSeconds(2));
            Assert.True(_youtube.AuthenticateCalls > calls);
            stopping = orchestrator.StopAsync();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StreamState.Idle, orchestrator.State);
            _youtube.AuthenticateOverride = null;
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var starts = _encoder.StartCalls.Count;
            auth.TrySetResult(true);
            Assert.Equal(starts, _encoder.StartCalls.Count);
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_encoder.IsRunning);
        }
        finally
        {
            auth.TrySetResult(true);
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_retention_does_not_overlap_the_live_supervisor_sweep()
    {
        CancellationToken runToken = default;
        _capture.StartOverride = token => { runToken = token; return Task.CompletedTask; };
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        _recording.SweepOverride = async token =>
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            try
            {
                if (_recording.RetentionRuns == 2)
                { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            }
            finally { Interlocked.Decrement(ref active); }
        };
        // Enter the actual common sweep boundary directly: timer order must not be a precondition.
        // Older builds called the backend directly; that legacy path must fail the same invariant.
        var sweep = typeof(StreamOrchestrator).GetMethod("SweepRetentionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Task RequestSweep() => sweep is null ? _recording.EnforceRetentionAsync(runToken)
            : Assert.IsAssignableFrom<Task>(sweep.Invoke(orchestrator, [runToken]));
        Task? first = null;
        Task? second = null;
        try
        {
            first = RequestSweep();
            Assert.True(entered.Task.IsCompleted);
            second = RequestSweep();
            Assert.False(second.IsCompleted); // a second caller cannot enter while the first owns the sweep
            Assert.Equal(2, _recording.RetentionRuns); // boot + the held first caller
            Assert.Equal(1, maximum);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, _recording.RetentionRuns);
            Assert.Equal(1, maximum);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(new[] { first, second }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(10));
            await orchestrator.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fallback_completion_budget_releases_stop_and_observes_a_late_fault(bool broadcastOnly)
    {
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new LateCompletionLog();
        _youtube.CompleteOverride = (id, token) => _youtube.CompleteCalls == 1 ? provider.Task : Task.CompletedTask;
        var orchestrator = CreateOrchestrator(broadcastCompletionTimeout: TimeSpan.FromMilliseconds(40), log: log);
        await orchestrator.StartAsync(CancellationToken.None);
        Task? ending = null;
        try
        {
            ending = broadcastOnly ? orchestrator.StopStreamingKeepRecordingAsync() : orchestrator.StopAsync();
            await ending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(provider.Task.IsCompleted);
            Assert.Equal(broadcastOnly ? StreamState.RecordingOnly : StreamState.Idle, orchestrator.State);
            Assert.Equal(broadcastOnly, _encoder.IsRunning);
            provider.TrySetException(new IOException("delayed-completion-fault"));
            await log.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(broadcastOnly ? StreamState.RecordingOnly : StreamState.Idle, orchestrator.State);
        }
        finally
        {
            provider.TrySetResult();
            if (ending is not null) await ending.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_pending_completion_cannot_clear_a_resumed_broadcast_clock()
    {
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.CompleteOverride = (id, token) => id == "bc-1" ? provider.Task : Task.CompletedTask;
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        var ending = orchestrator.StopStreamingKeepRecordingAsync();
        Task? resume = null;
        try
        {
            Assert.Equal(1, _youtube.CompleteCalls); // the old completion is held while the swap is owned
            resume = orchestrator.StartAsync(CancellationToken.None);
            Assert.Equal(2, _youtube.CreateCalls); // the new broadcast exists before the encoder swap is released
            var clock = typeof(StreamOrchestrator).GetField("_liveBroadcastStartedAtUtc",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var newClock = clock.GetValue(orchestrator);
            Assert.NotNull(newClock);
            provider.TrySetResult();
            await Task.WhenAll(ending, resume).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.Equal(newClock, clock.GetValue(orchestrator));
        }
        finally
        {
            provider.TrySetResult();
            await ending.WaitAsync(TimeSpan.FromSeconds(5));
            if (resume is not null) await resume.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_failed_completion_does_not_restore_a_resuming_session()
    {
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.CompleteOverride = (id, token) => id == "bc-1" ? provider.Task : Task.CompletedTask;
        _youtube.AuthenticateOverride = token => _youtube.AuthenticateCalls == 2 ? auth.Task : Task.FromResult(true);
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        var ending = orchestrator.StopStreamingKeepRecordingAsync();
        Task? resume = null;
        try
        {
            Assert.Equal(1, _youtube.CompleteCalls);
            resume = orchestrator.StartAsync(CancellationToken.None);
            Assert.Equal(2, _youtube.AuthenticateCalls);
            Assert.Equal(StreamState.ConnectingYouTube, orchestrator.State);
            provider.TrySetException(new IOException("old broadcast completion failed"));
            await ending.WaitAsync(TimeSpan.FromSeconds(5));
            auth.TrySetResult(true);
            await resume.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.Equal(2, _youtube.CreateCalls); // the old ending session must not be reused
        }
        finally
        {
            provider.TrySetResult();
            auth.TrySetResult(true);
            await ending.WaitAsync(TimeSpan.FromSeconds(5));
            if (resume is not null) await resume.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_duration_retry_cannot_hold_shutdown_on_an_ignoring_provider()
    {
        var provider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new LateCompletionLog();
        _youtube.CompleteFailuresBeforeSuccess = 1;
        _youtube.CompleteOverride = (id, token) => _youtube.CompleteCalls == 2 ? provider.Task : Task.CompletedTask;
        var orchestrator = CreateOrchestrator(liveAutoStop: TimeSpan.FromMilliseconds(30),
            liveAutoStopRetry: TimeSpan.FromMilliseconds(90),
            broadcastCompletionTimeout: TimeSpan.FromMilliseconds(40), log: log);
        await orchestrator.StartAsync(CancellationToken.None);
        Task? stopping = null;
        try
        {
            await WaitUntilAsync(() => _youtube.CompleteCalls >= 2, TimeSpan.FromSeconds(5));
            Assert.True(_youtube.CompleteCalls >= 2);
            stopping = orchestrator.StopAsync();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Idle, orchestrator.State);
            Assert.False(_encoder.IsRunning);
            Assert.False(provider.Task.IsCompleted);
            provider.TrySetException(new IOException("delayed-completion-fault"));
            await log.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StreamState.Idle, orchestrator.State);
        }
        finally
        {
            provider.TrySetResult();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_records_before_authentication_returns()
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(1, _youtube.AuthenticateCalls);
            Assert.True(_encoder.IsRunning);
            Assert.Equal(string.Empty, Assert.Single(_encoder.StartCalls).RtmpUrl);
            Assert.Equal(0, _youtube.CreateCalls);
        }
        finally { await orchestrator.StopAsync(); await start.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fallback_recovers_during_pending_auth_without_new_youtube_calls(bool stall)
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            _recording.ActiveRecordingLength = 1000;
            if (stall) _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
            else _encoder.SimulateDeath();
            await WaitUntilAsync(() => _encoder.StartCalls.Count >= 2 && _encoder.IsRunning, TimeSpan.FromSeconds(2));
            Assert.True(_encoder.IsRunning);
            Assert.Equal(2, _encoder.StartCalls.Count);
            Assert.All(_encoder.StartCalls, call => Assert.Equal(string.Empty, call.RtmpUrl));
            Assert.Equal(1, _youtube.AuthenticateCalls);
            Assert.Equal(0, _youtube.CreateCalls);
        }
        finally { await orchestrator.StopAsync(); await start.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Fallback_quiet_but_growing_recording_is_not_replaced_during_auth_wait()
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        _recording.GrowOnSample = true;
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
            var target = _recording.Samples + 4;
            await WaitUntilAsync(() => _recording.Samples >= target, TimeSpan.FromSeconds(3));
            Assert.True(_recording.Samples >= target);
            Assert.Single(_encoder.StartCalls);
            Assert.Equal(1, _youtube.AuthenticateCalls);
        }
        finally { await orchestrator.StopAsync(); await start.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Fallback_disabled_recording_stays_disabled_while_auth_is_pending()
    {
        var config = _configStore.Load(); config.Recording.Enabled = false; _configStore.Save(config);
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.Empty(_encoder.StartCalls);
            auth.TrySetResult(true);
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.Equal(string.Empty, Assert.Single(_encoder.StartCalls).RecordingFilePath);
        }
        finally { await orchestrator.StopAsync(); await start.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Fallback_recovery_and_live_transition_never_overlap_encoder_operations()
    {
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.AuthenticateOverride = token => auth.Task.WaitAsync(token);
        var recordingStarts = 0;
        _encoder.StartOverride = async (options, token) =>
        {
            if (options.RtmpUrl.Length == 0 && Interlocked.Increment(ref recordingStarts) == 2)
            {
                recoveryEntered.TrySetResult();
                await releaseRecovery.Task.WaitAsync(token);
            }
        };
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _youtube.AuthenticateCalls == 1, TimeSpan.FromSeconds(2));
            _encoder.SimulateDeath();
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            auth.TrySetResult(true);
            await WaitUntilAsync(() => _youtube.CreateCalls == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(1, _youtube.CreateCalls);
            Assert.False(start.IsCompleted); // live transition must wait for the recording swap
            releaseRecovery.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, _encoder.MaxConcurrentOperations);
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_encoder.IsRunning);
        }
        finally
        {
            releaseRecovery.TrySetResult();
            await orchestrator.StopAsync();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Fallback_late_broadcast_result_cannot_mutate_a_new_run()
    {
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<LiveSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _youtube.CreateOverride = token =>
        {
            if (_youtube.CreateCalls == 1) { created.TrySetResult(); return late.Task; }
            return Task.FromResult(new LiveSession("new-broadcast", "new-key", "rtmp://ingest.example/live2", "https://youtu.be/new"));
        };
        var orchestrator = CreateOrchestrator();
        var oldStart = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await created.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var count = _encoder.StartCalls.Count;
            late.TrySetResult(new LiveSession("old-broadcast", "old-key", "rtmp://ingest.example/live2", "https://youtu.be/old"));
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_encoder.IsRunning);
            Assert.Equal(count, _encoder.StartCalls.Count);
            Assert.Contains("new-key", _encoder.StartCalls[^1].RtmpUrl);
        }
        finally
        {
            late.TrySetResult(new LiveSession("old-broadcast", "old-key", "rtmp://ingest.example/live2", "https://youtu.be/old"));
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Audit_backup_restarts_after_each_failed_tee_start()
    {
        _encoder.StreamFailuresBeforeSuccess = 2;
        _encoder.OnStreamStartFailure = () =>
        {
            if (_encoder.FailedStreamStarts == 2) _youtube.AuthenticationUnavailable = true;
        };
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _encoder.FailedStreamStarts == 2 && _encoder.IsRunning,
                TimeSpan.FromSeconds(10)); // Observe recovery; do not assume a 1s CI scheduling budget.
            Assert.Equal(2, _encoder.FailedStreamStarts);
            Assert.True(_encoder.IsRunning);
            Assert.Equal(2, StartsAfterInitialBackup.Count(call => call.RtmpUrl.Length == 0));
            Assert.False(start.IsCompleted); // streaming remains offline, but the backup is alive
        }
        finally
        {
            await orchestrator.StopAsync();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_initialization_retries_transient_capture_or_retention_failure(bool retentionFailure)
    {
        if (retentionFailure) _recording.InitialFailuresBeforeSuccess = 1;
        else _capture.FailuresBeforeSuccess = 1;
        var orchestrator = CreateOrchestrator();
        try
        {
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_capture.Started);
            Assert.True(_recording.RetentionRuns >= 2);
            if (!retentionFailure)
            {
                Assert.Equal(2, _capture.StartCalls);
                Assert.True(_capture.StopCalls >= 1); // a partially initialized capture was released
            }
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task Audit_recording_only_alive_stall_restarts_without_reconnecting_youtube()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await orchestrator.StopStreamingKeepRecordingAsync();
            _recording.ActiveRecordingLength = 1000;
            _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
            await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 3, TimeSpan.FromSeconds(1));
            Assert.Equal(3, StartsAfterInitialBackup.Count);
            Assert.Equal(string.Empty, StartsAfterInitialBackup[^1].RtmpUrl);
            Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
            Assert.Equal(1, _youtube.CreateCalls);
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task Audit_recording_only_growth_keeps_a_quiet_encoder_running()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await orchestrator.StopStreamingKeepRecordingAsync();
            _recording.GrowOnSample = true;
            _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
            await Task.Delay(450);
            Assert.Equal(2, StartsAfterInitialBackup.Count);
            Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
        }
        finally { await orchestrator.StopAsync(); }
    }

    [Fact]
    public async Task Audit_late_cancelled_start_does_not_stop_a_new_live_session()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        _capture.StartOverride = async token =>
        {
            if (Interlocked.Increment(ref starts) != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await releaseOld.Task; // an old driver operation finishes cancellation late
                throw;
            }
        };
        var orchestrator = CreateOrchestrator();
        var oldStart = orchestrator.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
            releaseOld.TrySetResult();
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Live, orchestrator.State);
            Assert.True(_capture.Started);
            Assert.True(_encoder.IsRunning);
            Assert.Single(StartsAfterInitialBackup);
        }
        finally
        {
            releaseOld.TrySetResult();
            await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
            await orchestrator.StopAsync();
        }
    }

    [Fact]
    public async Task Audit_full_stop_during_recording_only_stall_does_not_start_a_late_encoder()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        var flush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await orchestrator.StopStreamingKeepRecordingAsync();
            _encoder.StopGate = flush;
            _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
            await WaitUntilAsync(() => !_encoder.IsRunning, TimeSpan.FromSeconds(1));
            Assert.False(_encoder.IsRunning); // watchdog owns the stalled encoder's delayed flush
            var stopping = orchestrator.StopAsync();
            flush.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StreamState.Idle, orchestrator.State);
            Assert.Equal(2, StartsAfterInitialBackup.Count);
            Assert.False(_encoder.IsRunning);
        }
        finally
        {
            flush.TrySetResult();
            await orchestrator.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_stop_cancels_initialization_retries_without_a_later_restart(bool retentionFailure)
    {
        if (retentionFailure) _recording.InitialFailuresBeforeSuccess = int.MaxValue;
        else _capture.FailuresBeforeSuccess = int.MaxValue;
        var orchestrator = CreateOrchestrator();
        var start = orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _recording.RetentionRuns >= 1, TimeSpan.FromSeconds(1));
        await orchestrator.StopAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        var attempts = _recording.RetentionRuns;
        await Task.Delay(100);
        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Equal(attempts, _recording.RetentionRuns);
        Assert.False(_capture.Started);
        Assert.Empty(_encoder.StartCalls);
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
        Assert.Equal(2, StartsAfterInitialBackup.Count);
        Assert.Equal(string.Empty, StartsAfterInitialBackup[1].RtmpUrl);
        Assert.EndsWith(".mp4", StartsAfterInitialBackup[1].RecordingFilePath);
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
    public async Task Ten_hour_guard_ends_broadcast_but_keeps_recording_and_records_replay()
    {
        var replayFile = Path.Combine(_dir, "live_replays.json");
        var replays = new LiveReplayCatalog(replayFile);
        var orchestrator = CreateOrchestrator(
            liveAutoStop: TimeSpan.FromMilliseconds(80),
            liveAutoStopRetry: TimeSpan.FromMilliseconds(250),
            liveReplays: replays);

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => orchestrator.State == StreamState.RecordingOnly,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, _youtube.CompleteCalls);
        Assert.True(_encoder.IsRunning);
        Assert.Equal(string.Empty, StartsAfterInitialBackup[^1].RtmpUrl);
        var replay = Assert.IsType<LiveReplayRecord>(replays.Latest);
        Assert.Equal("https://youtu.be/x", replay.Url);
        Assert.NotNull(replay.EndedAtUtc);

        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Eleven_hour_guard_retries_a_failed_youtube_completion()
    {
        _youtube.CompleteFailuresBeforeSuccess = 1;
        var replays = new LiveReplayCatalog(Path.Combine(_dir, "retry-live-replays.json"));
        var orchestrator = CreateOrchestrator(
            liveAutoStop: TimeSpan.FromMilliseconds(60),
            liveAutoStopRetry: TimeSpan.FromMilliseconds(150),
            liveReplays: replays);

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _youtube.CompleteCalls >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
        Assert.Equal(2, _youtube.CompleteCalls);
        Assert.NotNull(replays.Latest?.EndedAtUtc);

        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Duration_guard_fully_stops_without_deadlock_when_recording_is_disabled()
    {
        var config = _configStore.Load();
        config.Recording.Enabled = false;
        _configStore.Save(config);
        var orchestrator = CreateOrchestrator(
            liveAutoStop: TimeSpan.FromMilliseconds(60),
            liveAutoStopRetry: TimeSpan.FromMilliseconds(150));

        await orchestrator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => orchestrator.State == StreamState.Idle, TimeSpan.FromSeconds(5));

        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Equal(1, _youtube.CompleteCalls);
        Assert.False(_encoder.IsRunning);
    }

    [Fact]
    public async Task Live_start_from_recording_only_resumes_broadcast_without_restarting_capture()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        await orchestrator.StopStreamingKeepRecordingAsync();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.Equal(2, _youtube.CreateCalls); // the stopped broadcast was completed; this is a new one
        Assert.Equal(1, _youtube.CompleteCalls);
        Assert.Equal(3, StartsAfterInitialBackup.Count);
        Assert.Equal("rtmp://ingest.example/live2/key-2", StartsAfterInitialBackup[^1].RtmpUrl);
        Assert.True(_capture.Started);
        Assert.True(_mixer.Started);
        Assert.Equal(1, _capture.StartCalls);
        Assert.Equal(1, _mixer.StartCalls);

        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Stop_broadcast_from_non_live_is_a_no_op()
    {
        var orchestrator = CreateOrchestrator();

        await orchestrator.StopStreamingKeepRecordingAsync(); // Idle — nothing to drop

        Assert.Equal(StreamState.Idle, orchestrator.State);
        Assert.Empty(StartsAfterInitialBackup);
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
        Assert.Single(StartsAfterInitialBackup); // no recording-only restart
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
        Assert.Equal(2, StartsAfterInitialBackup.Count);
        Assert.Equal("rtmp://ingest.example/live2/key-1", StartsAfterInitialBackup[1].RtmpUrl);
        Assert.Contains("_part", StartsAfterInitialBackup[1].RecordingFilePath);
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

        Assert.Empty(StartsAfterInitialBackup);
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
        Assert.Single(StartsAfterInitialBackup);                 // no watchdog double-start
        Assert.Equal(StreamState.Live, orchestrator.State); // and no reconnect claim

        gate.SetResult();
        Assert.True(await swap);
        Assert.Equal(2, StartsAfterInitialBackup.Count); // exactly the swap's own restart
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
        Assert.Single(StartsAfterInitialBackup);

        // Let the growth stamp age past StallTimeout while the file size stays constant.
        await Task.Delay(300);

        // A new (small) part file replaces the active one while the progress line is quiet —
        // exactly the post-swap window. The shrink must re-baseline the growth signal instead of
        // being compared against the old length (which would trip a false stall restart).
        _recording.ActiveRecordingLength = 1_000_000;
        _recording.GrowOnSample = true;                       // …and it keeps growing normally
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // progress line still quiet

        await Task.Delay(300); // many watchdog ticks
        Assert.Single(StartsAfterInitialBackup); // no false restart
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_revives_a_dead_recording_only_encoder_without_reconnecting()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        await orchestrator.StopStreamingKeepRecordingAsync();
        Assert.Equal(2, StartsAfterInitialBackup.Count);

        _encoder.SimulateDeath();
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 3, TimeSpan.FromSeconds(5));

        // Recording is revived recording-only; the stream must NOT be re-established.
        Assert.Equal(string.Empty, StartsAfterInitialBackup[^1].RtmpUrl);
        Assert.EndsWith(".mp4", StartsAfterInitialBackup[^1].RecordingFilePath);
        Assert.Equal(StreamState.RecordingOnly, orchestrator.State);
        Assert.Equal(1, _youtube.CreateCalls); // no new broadcast
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restarts_a_dead_encoder()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(StartsAfterInitialBackup);

        _encoder.SimulateDeath();
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(StartsAfterInitialBackup.Count >= 2);
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Watchdog_restarts_a_stalled_encoder_even_while_process_is_alive()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(StartsAfterInitialBackup);

        // Process stays alive (IsRunning == true) but stops emitting progress AND the recording
        // file is not growing (FakeRecording default length stays 0): a genuinely stalled feed.
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5);
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(StartsAfterInitialBackup.Count >= 2);
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
        try
        {
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(StartsAfterInitialBackup);

        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // progress line quiet…
        // A busy CI worker may not schedule even one watchdog tick within 300 ms. Wait for
        // observed file samples, not elapsed wall time, before asserting the non-restart.
        await WaitUntilAsync(() => _recording.ActiveRecordingLength >= 3_000_000, TimeSpan.FromSeconds(5));

        Assert.True(_recording.ActiveRecordingLength >= 3_000_000);
        Assert.Single(StartsAfterInitialBackup); // …and recording is advancing, so no restart
        Assert.Equal(StreamState.Live, orchestrator.State);
        }
        finally { await orchestrator.StopAsync(); }
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
        Assert.Single(StartsAfterInitialBackup);

        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // no file signal → stats decide
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(StartsAfterInitialBackup.Count >= 2);
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
        Assert.Single(StartsAfterInitialBackup);

        _recording.GrowOnSample = false; // recording file frozen
        _encoder.TimeSinceProgress = TimeSpan.FromMinutes(5); // and the feed is quiet
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.True(StartsAfterInitialBackup.Count >= 2);
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
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        // The restart must reuse the existing broadcast, not spawn a new (orphan) one, and must
        // not complete it mid-session — that was the field-test "7 orphan broadcasts" bug.
        Assert.Equal(1, _youtube.CreateCalls);
        Assert.Null(_youtube.CompletedBroadcastId);

        await orchestrator.StopAsync();
        Assert.Equal("bc-1", _youtube.CompletedBroadcastId); // completed only at shutdown
    }

    [Fact]
    public async Task Controller_change_request_while_live_swaps_with_the_controllers_options()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // The controller lowered its desired level: every rebuilt options pass through Apply.
        _quality.ApplyOverride = o => o with { VideoBitrateKbps = 5400 };
        _quality.Raise(QualityStatus.Original with
        {
            Level = 1, LevelName = "절약 1단계", Reason = QualityChangeReason.NetworkCongestion
        });
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(5400, StartsAfterInitialBackup[^1].VideoBitrateKbps);
        Assert.Equal("rtmp://ingest.example/live2/key-1", StartsAfterInitialBackup[^1].RtmpUrl);
        await WaitUntilAsync(() => orchestrator.CurrentQuality.Level == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(StreamState.Live, orchestrator.State);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Controller_status_is_applied_and_published_on_session_start()
    {
        // A level desired before the session (e.g. deferred manual pin) applies on the next start.
        _quality.Status = QualityStatus.Original with { Level = 2, LevelName = "절약 2단계" };
        _quality.ApplyOverride = o => o with { VideoBitrateKbps = 3600, OutputFps = 30 };
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        var options = Assert.Single(StartsAfterInitialBackup);
        Assert.Equal(3600, options.VideoBitrateKbps);
        Assert.Equal(30, options.OutputFps);
        Assert.Equal(2, orchestrator.CurrentQuality.Level);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Change_request_at_the_applied_level_updates_status_without_a_swap()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // Mode-only change (manual pin at the SAME level): no pointless encoder restart.
        _quality.Raise(QualityStatus.Original with
        {
            Mode = QualityMode.ManualHold, Reason = QualityChangeReason.ManualSet
        });
        await WaitUntilAsync(
            () => orchestrator.CurrentQuality.Mode == QualityMode.ManualHold, TimeSpan.FromSeconds(5));

        Assert.Single(StartsAfterInitialBackup);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Rtmp_leg_failure_rebuilds_the_pipeline_reusing_the_broadcast()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        Assert.Single(StartsAfterInitialBackup);

        // onfail=ignore: the process keeps running and progress keeps flowing — only the sticky
        // leg-down signal betrays the silently dead broadcast (the -10053 field bug).
        _encoder.RtmpLegDown = true;
        await WaitUntilAsync(() => StartsAfterInitialBackup.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal("rtmp://ingest.example/live2/key-1", StartsAfterInitialBackup[^1].RtmpUrl);
        Assert.Equal(1, _youtube.CreateCalls); // same broadcast reused — no orphan
        Assert.Equal(StreamState.Live, orchestrator.State);
        Assert.False(_encoder.RtmpLegDown); // the fresh session reset the signal
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Rtmp_leg_rebuilds_are_backed_off_not_looped()
    {
        var orchestrator = CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);

        // The uplink stays dead: every rebuilt encoder immediately reports the leg down again.
        _encoder.KeepRtmpLegDownOnStart = true;
        _encoder.RtmpLegDown = true;
        await Task.Delay(500); // ~20 watchdog ticks — an unguarded loop would rebuild on each

        // Backoff (100→200→400ms cap in tests): a handful of spaced rebuilds, not a loop that
        // shreds the recording into part-file confetti.
        Assert.InRange(StartsAfterInitialBackup.Count, 2, 6);
        await orchestrator.StopAsync();
    }

    [Fact]
    public async Task Disabled_recording_streams_without_a_file()
    {
        var config = _configStore.Load();
        config.Recording.Enabled = false;
        _configStore.Save(config);
        var orchestrator = CreateOrchestrator();

        await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(StartsAfterInitialBackup).RecordingFilePath);
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // A completed assertion must not leave per-run watchdogs ticking in other tests.
        foreach (var orchestrator in _createdOrchestrators)
            await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- fakes ----

    private sealed class LateCompletionLog : ILogService
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message)
        {
            if (message.Contains("delayed-completion-fault", StringComparison.Ordinal)) Observed.TrySetResult();
        }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeYouTube : IYouTubeLiveService
    {
        public int FailuresBeforeSuccess;
        public int CreateCalls;
        public bool AuthenticationUnavailable;
        public int AuthenticateCalls;
        public Func<CancellationToken, Task<bool>>? AuthenticateOverride;
        public Func<CancellationToken, Task<LiveSession>>? CreateOverride;
        public int CompleteCalls;
        public int CompleteFailuresBeforeSuccess;
        public string? CompletedBroadcastId;
        public Func<string, CancellationToken, Task>? CompleteOverride;

        public Task<bool> AuthenticateAsync(CancellationToken ct)
        {
            AuthenticateCalls++;
            return AuthenticateOverride?.Invoke(ct) ?? Task.FromResult(!AuthenticationUnavailable);
        }

        public Task<LiveSession> CreateBroadcastAsync(CancellationToken ct)
        {
            CreateCalls++;
            if (CreateOverride is not null) return CreateOverride(ct);
            if (FailuresBeforeSuccess-- > 0)
            {
                throw new HttpRequestException("simulated network failure");
            }
            return Task.FromResult(new LiveSession(
                $"bc-{CreateCalls}", $"key-{CreateCalls}",
                "rtmp://ingest.example/live2", "https://youtu.be/x"));
        }

        public async Task CompleteBroadcastAsync(string broadcastId, CancellationToken ct)
        {
            CompleteCalls++;
            if (CompleteFailuresBeforeSuccess-- > 0)
            {
                throw new HttpRequestException("simulated completion failure");
            }
            if (CompleteOverride is not null) await CompleteOverride(broadcastId, ct);
            CompletedBroadcastId = broadcastId;
        }
    }

    private sealed class FakeEncoder : IEncoderPipeline
    {
        public List<EncoderStartOptions> StartCalls { get; } = [];
        public bool Stopped;
        public Func<EncoderStartOptions, CancellationToken, Task>? StartOverride;
        public int MaxConcurrentOperations;
        private int _operations;
        private void EnterOperation()
        {
            var active = Interlocked.Increment(ref _operations);
            int seen;
            do { seen = MaxConcurrentOperations; if (active <= seen) break; }
            while (Interlocked.CompareExchange(ref MaxConcurrentOperations, active, seen) != seen);
        }
        public int StreamFailuresBeforeSuccess;
        public int FailedStreamStarts;
        public Action? OnStreamStartFailure;
        private bool _running;

        public bool IsRunning => _running;

        /// <summary>Settable so tests can simulate a stalled feed (process alive, no progress).</summary>
        public TimeSpan TimeSinceProgress { get; set; } = TimeSpan.Zero;

        /// <summary>Sticky per-session RTMP-leg failure signal (mirrors EncoderPipeline).</summary>
        public bool RtmpLegDown { get; set; }

        /// <summary>Keeps RtmpLegDown asserted across restarts — simulates an uplink that stays dead.</summary>
        public bool KeepRtmpLegDownOnStart;

#pragma warning disable CS0067
        public event EventHandler<MetricsSnapshot>? MetricsUpdated;
        public event EventHandler? UnexpectedExit;
#pragma warning restore CS0067

        public async Task StartAsync(EncoderStartOptions options, CancellationToken ct)
        {
            EnterOperation();
            try
            {
            ct.ThrowIfCancellationRequested();
            StartCalls.Add(options);
            if (options.RtmpUrl.Length > 0 && StreamFailuresBeforeSuccess-- > 0)
            {
                FailedStreamStarts++;
                _running = false;
                OnStreamStartFailure?.Invoke();
                throw new IOException("simulated tee start failure");
            }
            if (StartOverride is not null) await StartOverride(options, ct);
            _running = true;
            TimeSinceProgress = TimeSpan.Zero; // a fresh start clears any prior stall
            if (!KeepRtmpLegDownOnStart)
            {
                RtmpLegDown = false; // a fresh session resets the sticky leg signal
            }
            }
            finally { Interlocked.Decrement(ref _operations); }
        }

        /// <summary>When set, the NEXT StopAsync awaits it — simulates ffmpeg's slow (~10s) flush.</summary>
        public TaskCompletionSource? StopGate;

        public async Task StopAsync()
        {
            EnterOperation();
            try
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
            finally { Interlocked.Decrement(ref _operations); }
        }

        public void SimulateDeath() => _running = false;

        public void Dispose() { }
    }

    private sealed class FakeRecording : IRecordingManager
    {
        public int RetentionRuns;
        public int InitialFailuresBeforeSuccess;
        public bool ThrowOnSweep; // throws on periodic sweeps (not the boot sweep)
        public int Samples;
        public Func<CancellationToken, Task>? SweepOverride;
        public long ActiveRecordingLength; // current recording-file size the watchdog samples
        public bool GrowOnSample; // simulate ffmpeg writing: grows on each sample (keyed to sampling, not wall-clock)
        private int _files;

        public string CreateSessionFilePath(DateTime sessionStartLocal) =>
            $"/rec/SilentStream_REC_{sessionStartLocal:yyyy-MM-dd_HHmm}{(++_files > 1 ? $"_part{_files}" : "")}.mp4";

        public RecordingStatus GetStatus() => RecordingStatus.Empty;

        public long GetActiveRecordingLength()
        {
            Interlocked.Increment(ref Samples);
            if (GrowOnSample)
            {
                ActiveRecordingLength += 1_000_000;
            }
            return ActiveRecordingLength;
        }

        public Task EnforceRetentionAsync(CancellationToken ct)
        {
            RetentionRuns++;
            if (InitialFailuresBeforeSuccess-- > 0) throw new IOException("simulated initial retention failure");
            if (ThrowOnSweep && RetentionRuns >= 2)
            {
                throw new IOException("simulated retention sweep failure");
            }
            return SweepOverride?.Invoke(ct) ?? Task.CompletedTask;
        }
    }

    /// <summary>Pass-through policy stub: tests drive it via ApplyOverride/Status/Raise.</summary>
    private sealed class FakeQuality : IAdaptiveQualityController
    {
        public QualityMode Mode => Status.Mode;
        public int Level => Status.Level;
        public IReadOnlyList<QualityStep> Ladder { get; } = [];
        public QualityStatus Status { get; set; } = QualityStatus.Original;
        public Func<EncoderStartOptions, EncoderStartOptions>? ApplyOverride;

        public event EventHandler<QualityStatus>? ChangeRequested;

        public EncoderStartOptions Apply(EncoderStartOptions baseOptions) =>
            ApplyOverride?.Invoke(baseOptions) ?? baseOptions;

        public void OnMetrics(MetricsSnapshot metrics) { }

        public void OnStateChanged(StreamState state) { }

        public void SetManual(int level) { }

        public void SetAuto() { }

        public void Raise(QualityStatus status)
        {
            Status = status;
            ChangeRequested?.Invoke(this, status);
        }
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public bool Started;
        public int StartCalls;
        public int FailuresBeforeSuccess;
        public int StopCalls;
        public Func<CancellationToken, Task>? StartOverride;
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
            StartCalls++;
            Started = true; // initialize a resource before a simulated failure
            if (FailuresBeforeSuccess-- > 0) throw new IOException("simulated capture initialization failure");
            return StartOverride?.Invoke(ct) ?? Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalls++;
            Started = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeMixer : IAudioMixer
    {
        public bool Started;
        public bool FailOnStart;
        public int StartCalls;

        public IReadOnlyList<AudioSourceSettings> Sources { get; private set; } = [];
        public IReadOnlyList<AudioSourceRuntimeStatus> SourceStatuses => Sources
            .Select(source => new AudioSourceRuntimeStatus(source.Id, Started, null, DateTime.UtcNow))
            .ToArray();
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
            StartCalls++;
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
