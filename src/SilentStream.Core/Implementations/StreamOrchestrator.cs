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

    /// <summary>
    /// Max time the encoder may run without emitting an ffmpeg progress line before the watchdog
    /// treats the feed as stalled (process alive but no longer encoding, e.g. capture/pipe EOF or a
    /// wedged ffmpeg). A stall only rebuilds the pipeline when the local recording file has also
    /// stopped growing (see SuperviseAsync). Must exceed the ffmpeg stats cadence. Note: an RTMP-only
    /// slave failure keeps progress flowing (onfail=ignore), so it is the encoder's concern, not this.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

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
    // Non-zero while StopStreamingKeepRecordingAsync is swapping the encoder (stop tee → start
    // file-only). The watchdog's RecordingOnly recovery must not see the intentional mid-swap
    // "encoder not running" window and double-start a second ffmpeg.
    private int _encoderSwapInProgress;

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
                // Apply the configured multi-source mixer layout (system + N mics, per-source
                // gain/mute/gate) before capture so headless auto-start matches the UI (plan §3.4).
                _audioMixer.ConfigureSources(AudioConfigMapper.ToSourceSettings(_configStore.Load().Audio));
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

        // Snapshot-and-clear under the gate: the broadcast-only swap also completes/clears the
        // session concurrently, and a check-then-dereference here could NRE past SetState(Idle),
        // bricking the state machine at Stopping. Completion failure must not block Idle either.
        LiveSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            try
            {
                using var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _youtube.CompleteBroadcastAsync(session.BroadcastId, completeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("브로드캐스트 종료 전이 실패 — 유튜브 스튜디오에서 상태를 확인하세요.", ex);
            }
        }

        SetState(StreamState.Idle);
        _log.Info("송출·녹화가 중지되었습니다.");
    }

    /// <summary>
    /// Stops only the YouTube broadcast; the local backup recording keeps running (원격 "방송만
    /// 중지"). The single-process tee encoder cannot drop one output at runtime, so the current
    /// mp4 is finalised and the encoder restarts in recording-only mode — recording continues in
    /// a NEW part file (frag mp4 cannot be appended). The broadcast is then completed so no
    /// feedless live lingers on the channel. Going live again requires 전체 중지 → 라이브 시작.
    /// </summary>
    public async Task StopStreamingKeepRecordingAsync()
    {
        if (!_configStore.Load().Recording.Enabled)
        {
            // Nothing to keep — a broadcast-only session degenerates to a full stop.
            await StopAsync().ConfigureAwait(false);
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            if (State != StreamState.Live)
            {
                return; // only an established live stream can be dropped in place
            }
            // Transition FIRST so the watchdog's Live-only restart branch can no longer resurrect
            // the RTMP leg while the encoder is being swapped below.
            SetState(StreamState.RecordingOnly);
            token = _runCts?.Token ?? CancellationToken.None;
        }

        Interlocked.Exchange(ref _encoderSwapInProgress, 1);
        try
        {
            await _encoder.StopAsync().ConfigureAwait(false);
            var config = _configStore.Load();
            // Re-stamp the session start: the new part file's position 0 is NOW. Without this,
            // every later period VOD cut computes its offset from the pre-swap start and lands
            // past EOF (silently dropping the cut).
            _sessionStart = DateTime.Now;
            var recordingPath = _recording.CreateSessionFilePath(_sessionStart);
            await _encoder.StartAsync(BuildEncoderOptions(string.Empty, recordingPath, config), token)
                .ConfigureAwait(false);

            // A concurrent 전체 중지 may have finished its whole teardown while this swap was
            // mid-flight (its encoder stop saw the OLD process). A freshly started encoder must
            // not outlive that teardown — nothing would supervise or ever stop it.
            bool tornDown;
            lock (_gate)
            {
                tornDown = State is StreamState.Stopping or StreamState.Idle;
            }
            if (tornDown)
            {
                await _encoder.StopAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return; // session shutdown raced us — StopAsync owns the teardown
        }
        catch (Exception ex)
        {
            // Best-effort: the watchdog's RecordingOnly branch revives a dead recording encoder.
            _log.Error("녹화 전용 전환 중 오류 — 워치독이 녹화를 복구합니다.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _encoderSwapInProgress, 0);
        }

        // Complete the broadcast so no feedless live lingers (mirrors StopAsync §4.3). Snapshot-
        // and-clear under the gate (a concurrent full stop also completes/clears); on failure the
        // session is restored so the eventual full StopAsync retries the completion.
        LiveSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            try
            {
                using var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _youtube.CompleteBroadcastAsync(session.BroadcastId, completeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("방송 종료 처리 실패 — 전체 중지 시 다시 시도합니다.", ex);
                lock (_gate)
                {
                    _session ??= session;
                }
            }
        }

        _log.Info("방송을 중지했습니다 — 로컬 백업 녹화는 계속됩니다.");
    }

    /// <summary>Infinite exponential-backoff connect loop (plan §4.4): 1→2→4…→60s cap.</summary>
    private async Task ConnectUntilLiveAsync(CancellationToken ct)
    {
        var delay = _options.RetryBaseDelay;
        var attempt = 0;
        var recordingStarted = false;

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

                // §3.6/§4.1: the local backup recording must not depend on YouTube. Once the
                // first connect attempt fails, bring up the encoder in recording-only mode so
                // recording keeps running while streaming retries (and even if it never
                // connects). StartStreamingOnceAsync switches it over to the RTMP+mp4 tee once
                // a broadcast is finally created.
                if (!recordingStarted)
                {
                    recordingStarted = await StartRecordingOnlyAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryMaxDelay.Ticks));
            }
        }
    }

    /// <summary>
    /// Brings the encoder up in recording-only mode (no RTMP) so the local backup survives
    /// YouTube being unreachable at boot (plan §3.6/§4.1). Best-effort: a recording failure
    /// must not stop streaming retries. Returns true once recording is settled — running,
    /// already running, or intentionally disabled — and false only on a transient start
    /// failure that should be retried on the next connect attempt.
    /// </summary>
    private async Task<bool> StartRecordingOnlyAsync(CancellationToken ct)
    {
        if (_encoder.IsRunning)
        {
            return true;
        }
        var config = _configStore.Load();
        if (!config.Recording.Enabled)
        {
            return true; // recording disabled — nothing to bring up.
        }

        try
        {
            // Stamp the session start at encoder start: StartLocal must map to the new file's
            // position 0, or period VOD cuts compute offsets from a stale start (past EOF).
            _sessionStart = DateTime.Now;
            var recordingPath = _recording.CreateSessionFilePath(_sessionStart);
            await _encoder.StartAsync(BuildEncoderOptions(string.Empty, recordingPath, config), ct)
                .ConfigureAwait(false);
            _log.Info("로컬 백업 녹화를 시작했습니다 (송출 연결과 독립).");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("로컬 녹화 시작 실패 — 송출 재시도는 계속합니다.", ex);
            return false;
        }
    }

    private async Task StartStreamingOnceAsync(CancellationToken ct)
    {
        if (!await _youtube.AuthenticateAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("YouTube 인증에 실패했습니다.");
        }

        // Reuse the existing broadcast across encoder restarts (watchdog/stall recovery). Creating
        // a fresh broadcast on every attempt is what orphaned 7 broadcasts in the field test and
        // silently migrated the stream to a new watch URL. EnableAutoStop=false keeps the broadcast
        // alive across a brief feed drop, so the restarted encoder just reconnects to the same
        // ingest key. A new broadcast is created only for a fresh session (none yet); the explicit
        // StopAsync at end of session is the only place it is completed.
        _session ??= await _youtube.CreateBroadcastAsync(ct).ConfigureAwait(false);

        var config = _configStore.Load();
        var recordingEnabled = config.Recording.Enabled;
        // Stamp the session start at (re)connect time: on a watchdog restart or a delayed first
        // connect the tee opens a NEW part file whose position 0 is now, and a stale StartLocal
        // would shift every later period VOD cut past EOF (same fix as the broadcast-only swap).
        _sessionStart = DateTime.Now;
        var recordingPath = recordingEnabled
            ? _recording.CreateSessionFilePath(_sessionStart)
            : string.Empty;

        if (_encoder.IsRunning)
        {
            await _encoder.StopAsync().ConfigureAwait(false);
        }

        await _encoder.StartAsync(
            BuildEncoderOptions($"{_session.RtmpUrl}/{_session.StreamKey}", recordingPath, config), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the encoder options from the live capture dimensions and the manual encoding config
    /// (resolution/fps/bitrate overrides; "source"/0 = follow the capture). The output dims/fps are
    /// only set when they differ from the capture, so the default path encodes 1:1.
    /// </summary>
    private EncoderStartOptions BuildEncoderOptions(string rtmpUrl, string recordingPath, AppConfig config)
    {
        var capWidth = _capture.Width;
        var capHeight = _capture.Height;
        var capFps = _capture.Fps;

        var (outWidth, outHeight) = ParseResolution(config.Encoding.Resolution, capWidth, capHeight);
        var outFps = ParseFps(config.Encoding.Fps, capFps);
        var videoBitrate = config.Encoding.VideoBitrateKbps > 0
            ? config.Encoding.VideoBitrateKbps
            : BitrateMapper.GetVideoBitrateKbps(outHeight, outFps);
        var audioBitrate = config.Encoding.AudioBitrateKbps > 0 ? config.Encoding.AudioBitrateKbps : 160;

        return new EncoderStartOptions(
            RtmpUrl: rtmpUrl,
            RecordingFilePath: recordingPath,
            VideoBitrateKbps: videoBitrate,
            Width: capWidth,
            Height: capHeight,
            Fps: capFps,
            AudioBitrateKbps: audioBitrate,
            AudioFilters: AudioConfigMapper.ToFilterSettings(config.Audio.Filters),
            OutputWidth: outWidth == capWidth ? 0 : outWidth,
            OutputHeight: outHeight == capHeight ? 0 : outHeight,
            OutputFps: Math.Abs(outFps - capFps) < 0.01 ? 0 : outFps);
    }

    /// <summary>Parses "WxH" (else "source") into even encode dimensions, clamped to the capture.</summary>
    private static (int Width, int Height) ParseResolution(string? resolution, int capWidth, int capHeight)
    {
        if (string.IsNullOrEmpty(resolution) || resolution.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            return (capWidth, capHeight);
        }
        var parts = resolution.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 1 && h > 1)
        {
            // Clamp to the capture (never upscale — mirrors ParseFps) and force even dims.
            var cw = Math.Min(w, capWidth);
            var ch = Math.Min(h, capHeight);
            return (cw - cw % 2, ch - ch % 2);
        }
        return (capWidth, capHeight);
    }

    /// <summary>Parses a numeric fps (else "source"), clamped to the capture rate (can't upsample).</summary>
    private static double ParseFps(string? fps, double capFps)
    {
        if (string.IsNullOrEmpty(fps) || fps.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            return capFps;
        }
        return double.TryParse(fps, System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0
            ? Math.Min(f, capFps)
            : capFps;
    }

    /// <summary>
    /// Watchdog + housekeeping loop (plan §4.4): restarts a dead encoder (same-session
    /// part file via CreateSessionFilePath) and sweeps recording retention hourly. A stalled
    /// feed (process alive, no ffmpeg progress) only triggers a restart when the local recording
    /// file has <em>also</em> stopped growing, so a static-screen + silent-audio session that merely
    /// quietens ffmpeg's stats line is not churned into a ~30s restart loop. Recording-off sessions
    /// have no file signal and fall back to the stats line alone (original behaviour).
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.UtcNow;
        var lastRecordingLength = -1L;
        var lastRecordingGrowthAt = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            // The whole body is guarded: an unhandled fault here must never silently kill the
            // supervisor (a field session sat idle ~2h after exactly that). Only cancellation ends it.
            try
            {
                await Task.Delay(_options.WatchdogInterval, ct).ConfigureAwait(false);

                // Second liveness signal: is the local recording file still growing? A file that
                // keeps growing proves the encoder is muxing frames even when its progress line has
                // gone quiet. 0 length (recording off / not started yet) yields no signal → stats decide.
                var recordingLength = _recording.GetActiveRecordingLength();
                if (recordingLength > lastRecordingLength)
                {
                    lastRecordingGrowthAt = DateTime.UtcNow;
                }
                lastRecordingLength = recordingLength;
                var recordingAdvancing = recordingLength > 0 &&
                    DateTime.UtcNow - lastRecordingGrowthAt <= _options.StallTimeout;

                if (State == StreamState.Live)
                {
                    string? restartReason = null;
                    if (!_encoder.IsRunning)
                    {
                        restartReason = "인코더 프로세스 사망"; // process gone → restart immediately
                    }
                    else if (_encoder.TimeSinceProgress > _options.StallTimeout && !recordingAdvancing)
                    {
                        restartReason = "인코더 피드 정지(스톨)"; // feed quiet AND recording frozen → real stall
                    }

                    if (restartReason is not null)
                    {
                        // Claim the pipeline under the gate before reconnecting: 방송만 중지 takes the
                        // same gate to leave Live, so exactly one side wins — the watchdog can never
                        // resurrect a broadcast the operator just dropped (unlocked re-checks cannot
                        // guarantee that; this transition can).
                        bool proceed;
                        lock (_gate)
                        {
                            proceed = State == StreamState.Live;
                            if (proceed)
                            {
                                SetState(StreamState.ConnectingYouTube);
                            }
                        }
                        if (proceed)
                        {
                            _log.Warn($"{restartReason} 감지 — 파이프라인을 재구성합니다.");
                            await ConnectUntilLiveAsync(ct).ConfigureAwait(false);
                            // Re-baseline: the new _part file restarts near 0 bytes and the encoder's
                            // progress clock is reset by StartAsync, so the next ticks see a fresh feed and
                            // a small file without either signal falsely re-tripping the stall branch.
                            lastRecordingLength = -1L;
                            lastRecordingGrowthAt = DateTime.UtcNow;
                        }
                    }
                }
                else if (State == StreamState.RecordingOnly && !_encoder.IsRunning &&
                         Volatile.Read(ref _encoderSwapInProgress) == 0)
                {
                    // 방송만 중지 후 남은 녹화 전용 인코더가 죽으면 녹화만 되살린다 — 스트림 재수립 금지.
                    // (교체가 진행 중인 의도적 정지 구간은 _encoderSwapInProgress 플래그로 건너뛴다.)
                    _log.Warn("녹화 인코더 사망 감지 — 녹화를 재시작합니다.");
                    await StartRecordingOnlyAsync(ct).ConfigureAwait(false);
                    lastRecordingLength = -1L;
                    lastRecordingGrowthAt = DateTime.UtcNow;
                }

                if (DateTime.UtcNow - lastSweep >= _options.RetentionInterval)
                {
                    lastSweep = DateTime.UtcNow;
                    await _recording.EnforceRetentionAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("워치독 루프 오류 — 감시를 계속합니다.", ex);
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
