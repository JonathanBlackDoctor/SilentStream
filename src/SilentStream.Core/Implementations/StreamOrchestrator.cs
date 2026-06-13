using System.Diagnostics;
using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>Timing knobs for the orchestrator; tests shrink these to milliseconds.</summary>
public sealed record StreamOrchestratorOptions
{
    /// <summary>Network-stabilisation delay after boot (plan §3.1: 30초).</summary>
    public TimeSpan WarmupDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>First retry delay; doubles up to <see cref="RetryMaxDelay"/> (plan §4.4).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Exponential backoff cap (plan §4.4: 최대 60초).</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Encoder watchdog poll interval (plan §4.4).</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Recording retention sweep interval (plan §3.6: 1시간 주기).</summary>
    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Runtime central coordinator (plan §4): drives the state machine
/// Idle → Warmup(30s) → ConnectingYouTube(🟡) → Live(🟢), with infinite
/// exponential-backoff retries (🔴) on stream failure, an encoder watchdog, hourly
/// recording-retention sweeps, and a graceful stop sequence. Recording runs inside the
/// encoder's tee output, so RTMP failures never interrupt the local file (§3.6).
/// </summary>
public sealed class StreamOrchestrator : IStreamOrchestrator
{
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly IYouTubeLiveService _youtube;
    private readonly IEncoderPipeline _encoder;
    private readonly IRecordingManager _recording;
    private readonly IScreenCaptureSource _capture;
    private readonly IAudioMixer _audioMixer;
    private readonly StreamOrchestratorOptions _options;

    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _supervisorTask;
    private LiveSession? _session;
    private DateTime _sessionStart;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSample;

    public StreamOrchestrator(
        IConfigStore configStore,
        ILogService log,
        IYouTubeLiveService youtube,
        IEncoderPipeline encoder,
        IRecordingManager recording,
        IScreenCaptureSource capture,
        IAudioMixer audioMixer)
        : this(configStore, log, youtube, encoder, recording, capture, audioMixer,
            new StreamOrchestratorOptions())
    {
    }

    public StreamOrchestrator(
        IConfigStore configStore,
        ILogService log,
        IYouTubeLiveService youtube,
        IEncoderPipeline encoder,
        IRecordingManager recording,
        IScreenCaptureSource capture,
        IAudioMixer audioMixer,
        StreamOrchestratorOptions options)
    {
        _configStore = configStore;
        _log = log;
        _youtube = youtube;
        _encoder = encoder;
        _recording = recording;
        _capture = capture;
        _audioMixer = audioMixer;
        _options = options;

        _encoder.MetricsUpdated += OnEncoderMetrics;
    }

    public StreamState State { get; private set; } = StreamState.Idle;

    public event EventHandler<StreamState>? StateChanged;
    public event EventHandler<MetricsSnapshot>? MetricsUpdated;

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (State != StreamState.Idle)
            {
                return;
            }
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetState(StreamState.Warmup);
        }
        var token = _runCts!.Token;

        try
        {
            await Task.Delay(_options.WarmupDelay, token).ConfigureAwait(false);

            // Recording bookkeeping first: it must survive any streaming failure.
            _sessionStart = DateTime.Now;
            await _recording.EnforceRetentionAsync(token).ConfigureAwait(false);

            await _capture.StartAsync(token).ConfigureAwait(false);
            try
            {
                await _audioMixer.StartAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // §4.4: audio device trouble must not stop the show.
                _log.Error("오디오 초기화 실패 — 영상만으로 계속합니다.", ex);
            }

            await ConnectUntilLiveAsync(token).ConfigureAwait(false);

            _supervisorTask = SuperviseAsync(token);
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (State is StreamState.Idle or StreamState.Stopping)
            {
                return;
            }
            SetState(StreamState.Stopping);
            cts = _runCts;
        }

        cts?.Cancel();
        if (_supervisorTask is not null)
        {
            try
            {
                await _supervisorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _supervisorTask = null;
        }

        // §4.3 shutdown order: finalise the mp4 first, then complete the broadcast.
        try
        {
            await _encoder.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("인코더 정지 중 오류", ex);
        }

        try
        {
            await _capture.StopAsync().ConfigureAwait(false);
            await _audioMixer.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("캡처/오디오 정지 중 오류", ex);
        }

        if (_session is not null)
        {
            using var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _youtube.CompleteBroadcastAsync(_session.BroadcastId, completeCts.Token)
                .ConfigureAwait(false);
            _session = null;
        }

        SetState(StreamState.Idle);
        _log.Info("송출·녹화가 중지되었습니다.");
    }

    /// <summary>Infinite exponential-backoff connect loop (plan §4.4): 1→2→4…→60s cap.</summary>
    private async Task ConnectUntilLiveAsync(CancellationToken ct)
    {
        var delay = _options.RetryBaseDelay;
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SetState(StreamState.ConnectingYouTube);
            attempt++;

            try
            {
                await StartStreamingOnceAsync(ct).ConfigureAwait(false);
                SetState(StreamState.Live);
                _log.Info($"라이브 송출 시작 (시도 {attempt}회차)");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetState(StreamState.Retrying);
                _log.Warn($"송출 시작 실패({ex.Message}) — {delay.TotalSeconds:F0}초 후 재시도");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryMaxDelay.Ticks));
            }
        }
    }

    private async Task StartStreamingOnceAsync(CancellationToken ct)
    {
        if (!await _youtube.AuthenticateAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("YouTube 인증에 실패했습니다.");
        }

        _session = await _youtube.CreateBroadcastAsync(ct).ConfigureAwait(false);

        var recordingEnabled = _configStore.Load().Recording.Enabled;
        var recordingPath = recordingEnabled
            ? _recording.CreateSessionFilePath(_sessionStart)
            : string.Empty;

        var width = _capture.Width;
        var height = _capture.Height;
        var fps = _capture.Fps;

        if (_encoder.IsRunning)
        {
            await _encoder.StopAsync().ConfigureAwait(false);
        }

        await _encoder.StartAsync(new EncoderStartOptions(
            RtmpUrl: $"{_session.RtmpUrl}/{_session.StreamKey}",
            RecordingFilePath: recordingPath,
            VideoBitrateKbps: BitrateMapper.GetVideoBitrateKbps(height, fps),
            Width: width,
            Height: height,
            Fps: fps), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Watchdog + housekeeping loop (plan §4.4): restarts a dead encoder (same-session
    /// part file via CreateSessionFilePath) and sweeps recording retention hourly.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.WatchdogInterval, ct).ConfigureAwait(false);

            if (State == StreamState.Live && !_encoder.IsRunning)
            {
                _log.Warn("인코더 프로세스 사망 감지 — 파이프라인을 재구성합니다.");
                try
                {
                    await ConnectUntilLiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (DateTime.UtcNow - lastSweep >= _options.RetentionInterval)
            {
                lastSweep = DateTime.UtcNow;
                try
                {
                    await _recording.EnforceRetentionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"녹화 보존 정리 실패: {ex.Message}");
                }
            }
        }
    }

    private void OnEncoderMetrics(object? sender, MetricsSnapshot metrics) =>
        MetricsUpdated?.Invoke(this, metrics with { CpuPercent = SampleCpuPercent() });

    /// <summary>Process CPU% across all cores since the previous metrics tick.</summary>
    private double SampleCpuPercent()
    {
        try
        {
            var now = DateTime.UtcNow;
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            double percent = 0;
            if (_lastCpuSample != default)
            {
                var wall = (now - _lastCpuSample).TotalMilliseconds * Environment.ProcessorCount;
                if (wall > 0)
                {
                    percent = (cpu - _lastCpuTime).TotalMilliseconds / wall * 100;
                }
            }
            _lastCpuTime = cpu;
            _lastCpuSample = now;
            return Math.Clamp(percent, 0, 100);
        }
        catch (PlatformNotSupportedException)
        {
            return 0;
        }
    }

    private void SetState(StreamState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
