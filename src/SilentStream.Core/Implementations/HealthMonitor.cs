using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// The health/event layer (원격 컨트롤러 개선 로드맵 Phase 0). Consolidates the app's scattered health
/// signals into one typed <see cref="HealthEvent"/> stream with severity + debounce/hysteresis:
/// <list type="bullet">
///   <item>rtmp_down / live_started / live_stopped — derived from <see cref="IStreamOrchestrator.StateChanged"/>.</item>
///   <item>mic_silent — from <see cref="IAudioMixer.MicSignalChanged"/> (the mixer already grace-debounces).</item>
///   <item>disk_low — POLLED from <see cref="IRecordingManager.GetStatus"/> vs config MinFreeGb (no event exists).</item>
///   <item>upload_failed — POLLED from <see cref="IUploadQueue.Snapshot"/> for terminal failures (no event exists).</item>
/// </list>
/// oauth_expiring comes from the shared <see cref="IYouTubeAuthHealth"/> source: it warns ten
/// minutes before the observed access-token expiry and becomes critical when renewal is rejected.
/// Thread-safe (single <c>_gate</c> lock guards all debounce state, mirroring PairingThrottle); source
/// events arrive on background threads and are never marshalled to the UI. The clock and poll delay are
/// injectable so debounce/hysteresis is unit-testable without real time (PeriodScheduler/PairingThrottle seam).
/// </summary>
public sealed class HealthMonitor : IHealthMonitor
{
    private const long GB = 1024L * 1024 * 1024;

    // Cadence + hysteresis windows. rtmp_down must persist past the warn window before we notify, so a
    // brief watchdog reconnect does not alarm; it escalates to critical if it keeps failing.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RtmpDownWarnAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RtmpDownCriticalAfter = TimeSpan.FromSeconds(60);
    private const long DiskLowClearMarginBytes = GB; // recover this far above the threshold before clearing

    private readonly IStreamOrchestrator _orchestrator;
    private readonly IAudioMixer _mixer;
    private readonly IRecordingManager _recording;
    private readonly IUploadQueue _uploadQueue;
    private readonly IConfigStore _configStore;
    private readonly IYouTubeAuthHealth? _youtubeAuth;
    private readonly ISplitApprovalService? _splits; // null in older tests — split checks just skip
    private readonly ILogService _log;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _gate = new();
    private int _started;
    private int _disposed;
    private CancellationTokenSource? _cts;
    private string? _room;

    // Debounce/hysteresis state — all mutated only under _gate.
    private bool _liveSessionActive;
    private bool _qualityDegradedActive;
    private DateTime? _retryingSinceUtc;
    private HealthSeverity? _rtmpDownSeverity;
    private HealthSeverity? _diskLowSeverity;
    private HealthSeverity? _oauthSeverity;
    private readonly HashSet<string> _silentMics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedFailedUploadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedPendingSplitIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HealthEvent> _active = new(StringComparer.Ordinal);

    public event EventHandler<HealthEvent>? HealthChanged;

    /// <summary>Production constructor (DI selects this — the Func seams below are not registered).</summary>
    public HealthMonitor(
        IStreamOrchestrator orchestrator,
        IAudioMixer mixer,
        IRecordingManager recording,
        IUploadQueue uploadQueue,
        IConfigStore configStore,
        IYouTubeAuthHealth youtubeAuth,
        ISplitApprovalService splits,
        ILogService log)
        : this(orchestrator, mixer, recording, uploadQueue, configStore, log,
            () => DateTime.UtcNow, Task.Delay, splits, youtubeAuth)
    {
    }

    /// <summary>Test seam: inject a virtual clock and delay so debounce/hysteresis can be driven deterministically.</summary>
    public HealthMonitor(
        IStreamOrchestrator orchestrator,
        IAudioMixer mixer,
        IRecordingManager recording,
        IUploadQueue uploadQueue,
        IConfigStore configStore,
        ILogService log,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay,
        ISplitApprovalService? splits = null,
        IYouTubeAuthHealth? youtubeAuth = null)
    {
        _orchestrator = orchestrator;
        _mixer = mixer;
        _recording = recording;
        _uploadQueue = uploadQueue;
        _configStore = configStore;
        _youtubeAuth = youtubeAuth;
        _splits = splits;
        _log = log;
        _now = now;
        _delay = delay;
    }

    public IReadOnlyList<HealthEvent> ActiveEvents
    {
        get { lock (_gate) { return _active.Values.ToList(); } }
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return; // idempotent (mirrors VodCoordinator)
        }

        _orchestrator.StateChanged += OnStateChanged;
        _orchestrator.QualityChanged += OnQualityChanged;
        _mixer.MicSignalChanged += OnMicSignalChanged;
        if (_youtubeAuth is not null)
        {
            _youtubeAuth.StatusChanged += OnYouTubeAuthStatusChanged;
        }

        // Seed the baseline so we don't emit a spurious live_started, and pick up an in-progress outage.
        // _room is (re)read here and refreshed each poll pass so a runtime 호실명 edit is reflected.
        lock (_gate)
        {
            _room = NormalizeRoom(_configStore.Load().DeviceName);
            var state = _orchestrator.State;
            _liveSessionActive = state == StreamState.Live;
            if (state == StreamState.Retrying)
            {
                _retryingSinceUtc = _now();
            }
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PollLoopAsync(_cts.Token);
        _log.Info("헬스 모니터를 시작했습니다.");
    }

    // ---- Event-driven signals (orchestrator state, mic signal) ----

    private void OnStateChanged(object? sender, StreamState state)
    {
        var buffer = new List<HealthEvent>();
        lock (_gate)
        {
            switch (state)
            {
                case StreamState.Live:
                    // Reaching Live clears any active outage and (once) marks the session started.
                    if (_rtmpDownSeverity is not null)
                    {
                        _rtmpDownSeverity = null;
                        SetCondition(buffer, HealthEventKind.RtmpDown, HealthSeverity.Info, active: false,
                            "송출 연결이 정상 복구되었습니다.", sourceKey: null);
                    }
                    _retryingSinceUtc = null;
                    if (!_liveSessionActive)
                    {
                        _liveSessionActive = true;
                        Notify(buffer, HealthEventKind.LiveStarted, HealthSeverity.Info,
                            "라이브 송출을 시작했습니다.", sourceKey: null);
                        // A mic that fell silent before going live must escalate Warn→Critical now.
                        RestampSilentMics(buffer, HealthSeverity.Critical);
                    }
                    break;

                case StreamState.Retrying:
                    // Record when the outage began; the actual rtmp_down emit waits for the poll's hysteresis.
                    _retryingSinceUtc ??= _now();
                    break;

                case StreamState.Idle:
                case StreamState.Stopping:
                    if (_rtmpDownSeverity is not null)
                    {
                        _rtmpDownSeverity = null;
                        SetCondition(buffer, HealthEventKind.RtmpDown, HealthSeverity.Info, active: false,
                            "송출을 중지했습니다.", sourceKey: null);
                    }
                    _retryingSinceUtc = null;
                    if (_liveSessionActive)
                    {
                        _liveSessionActive = false;
                        Notify(buffer, HealthEventKind.LiveStopped, HealthSeverity.Info,
                            "라이브 송출이 종료되었습니다.", sourceKey: null);
                        // Session ended — a still-silent mic drops back from Critical to Warn.
                        RestampSilentMics(buffer, HealthSeverity.Warn);
                    }
                    break;

                case StreamState.RecordingOnly:
                    // 방송만 중지: the live session is over while recording continues. Clear any
                    // outage bookkeeping and announce the stop; the later Stopping/Idle transition
                    // must not emit a second live_stopped (_liveSessionActive is already false).
                    if (_rtmpDownSeverity is not null)
                    {
                        _rtmpDownSeverity = null;
                        SetCondition(buffer, HealthEventKind.RtmpDown, HealthSeverity.Info, active: false,
                            "송출을 중지했습니다.", sourceKey: null);
                    }
                    _retryingSinceUtc = null;
                    if (_liveSessionActive)
                    {
                        _liveSessionActive = false;
                        Notify(buffer, HealthEventKind.LiveStopped, HealthSeverity.Info,
                            "라이브 송출이 종료되었습니다 — 로컬 녹화는 계속됩니다.", sourceKey: null);
                        RestampSilentMics(buffer, HealthSeverity.Warn);
                    }
                    break;

                // Warmup / ConnectingYouTube are transitional — no health event.
            }
        }
        RaiseAll(buffer);
    }

    /// <summary>
    /// Mirrors the applied quality into the QualityDegraded condition (확장계획서_적응형송출품질
    /// §8): active while an AUTOMATIC degradation is in effect (Warn, Critical at the 안전 모드
    /// floor so the deepening pushes past the notifier's escalation de-dup), cleared on recovery
    /// to 원본 or when an operator pins the level manually; a manual pin itself is a momentary
    /// Info. The notifier already formats by severity/active only, so no mapping there changes.
    /// </summary>
    private void OnQualityChanged(object? sender, QualityStatus quality)
    {
        var buffer = new List<HealthEvent>();
        lock (_gate)
        {
            var autoDegraded = quality.Level > 0 && quality.Mode == QualityMode.Auto;
            if (autoDegraded)
            {
                _qualityDegradedActive = true;
                var severity = quality.Level >= 3 ? HealthSeverity.Critical : HealthSeverity.Warn;
                var applied = quality.Applied is { } step ? $"({step.VideoBitrateKbps}kbps)" : string.Empty;
                SetCondition(buffer, HealthEventKind.QualityDegraded, severity, active: true,
                    $"송출 품질을 자동으로 낮췄습니다: {quality.LevelName}{applied} — {ReasonText(quality.Reason)}",
                    sourceKey: null);
            }
            else if (_qualityDegradedActive)
            {
                _qualityDegradedActive = false;
                var message = quality.Mode == QualityMode.ManualHold
                    ? $"품질 자동 하강 상태를 수동 고정으로 전환했습니다: {quality.LevelName}"
                    : "송출 품질이 원본으로 복구되었습니다.";
                SetCondition(buffer, HealthEventKind.QualityDegraded, HealthSeverity.Info, active: false,
                    message, sourceKey: null);
            }
            else if (quality.Mode == QualityMode.ManualHold &&
                     quality.Reason == QualityChangeReason.ManualSet)
            {
                Notify(buffer, HealthEventKind.QualityDegraded, HealthSeverity.Info,
                    $"송출 품질을 수동 설정했습니다: {quality.LevelName}", sourceKey: null);
            }
        }
        RaiseAll(buffer);
    }

    private static string ReasonText(QualityChangeReason reason) => reason switch
    {
        QualityChangeReason.EncodeOverload => "기기 부하(인코딩 지연) 감지",
        QualityChangeReason.NetworkCongestion => "네트워크 혼잡(전송 패킷 드롭) 감지",
        QualityChangeReason.AutoRecover => "여건 회복",
        QualityChangeReason.ManualSet => "수동 설정",
        QualityChangeReason.SessionReset => "세션 종료",
        _ => "상태 변화"
    };

    private void OnMicSignalChanged(object? sender, MicSignalStatus status)
    {
        var buffer = new List<HealthEvent>();
        lock (_gate)
        {
            if (!status.SignalPresent)
            {
                if (_silentMics.Add(status.SourceId))
                {
                    // A silent mic during a live broadcast is more urgent than while idle.
                    var severity = _liveSessionActive ? HealthSeverity.Critical : HealthSeverity.Warn;
                    SetCondition(buffer, HealthEventKind.MicSilent, severity, active: true,
                        $"마이크 신호가 없습니다: {ResolveMicName(status.SourceId)} — 연결/음소거를 확인하세요.",
                        status.SourceId);
                }
            }
            else if (_silentMics.Remove(status.SourceId))
            {
                SetCondition(buffer, HealthEventKind.MicSilent, HealthSeverity.Info, active: false,
                    $"마이크 신호가 복구되었습니다: {ResolveMicName(status.SourceId)}", status.SourceId);
            }
        }
        RaiseAll(buffer);
    }

    private void OnYouTubeAuthStatusChanged(object? sender, YouTubeAuthHealthStatus status)
    {
        var buffer = new List<HealthEvent>();
        lock (_gate)
        {
            switch (status.State)
            {
                case YouTubeAuthHealthState.Healthy:
                    if (_oauthSeverity is not null)
                    {
                        _oauthSeverity = null;
                        SetCondition(buffer, HealthEventKind.OauthExpiring, HealthSeverity.Info,
                            active: false, status.Message, sourceKey: null);
                    }
                    break;

                case YouTubeAuthHealthState.Expiring:
                    _oauthSeverity = HealthSeverity.Warn;
                    SetCondition(buffer, HealthEventKind.OauthExpiring, HealthSeverity.Warn,
                        active: true, status.Message, sourceKey: null);
                    break;

                case YouTubeAuthHealthState.ActionRequired:
                    _oauthSeverity = HealthSeverity.Critical;
                    SetCondition(buffer, HealthEventKind.OauthExpiring, HealthSeverity.Critical,
                        active: true, status.Message, sourceKey: null);
                    break;
            }
        }
        RaiseAll(buffer);
    }

    // ---- Polled signals (disk, uploads) + rtmp_down hysteresis ----

    /// <summary>
    /// Runs one health-poll pass: disk-space + upload-failure checks and the rtmp_down hysteresis
    /// evaluation. Invoked by the internal poll loop every <see cref="PollInterval"/>; also exposed so
    /// unit tests can drive it deterministically after advancing the virtual clock.
    /// </summary>
    public void RunChecksOnce()
    {
        var buffer = new List<HealthEvent>();

        try
        {
            // This may synchronously raise StatusChanged; it stays outside this monitor's lock so
            // the handler can safely create the OauthExpiring health event.
            _youtubeAuth?.Evaluate();
        }
        catch (Exception ex)
        {
            _log.Error("OAuth health expiry check failed", ex);
        }

        // Gather external inputs OUTSIDE the lock so disk I/O never blocks the event handlers.
        var state = _orchestrator.State;

        long? freeBytes = null;
        var recordingEnabled = false;
        long thresholdBytes = 0;
        string? room = null;
        var roomKnown = false;
        try
        {
            var config = _configStore.Load();
            room = NormalizeRoom(config.DeviceName); // refresh the room stamp so a runtime 호실명 edit is picked up
            roomKnown = true;
            recordingEnabled = config.Recording.Enabled;
            thresholdBytes = (long)Math.Max(0, config.Recording.MinFreeGb) * GB;
            freeBytes = _recording.GetStatus().FreeDiskBytes;
        }
        catch (Exception ex)
        {
            _log.Error("헬스 모니터: 디스크 상태 조회 실패", ex);
        }

        IReadOnlyList<UploadJob>? jobs = null;
        try
        {
            jobs = _uploadQueue.Snapshot();
        }
        catch (Exception ex)
        {
            _log.Error("헬스 모니터: 업로드 큐 조회 실패", ex);
        }

        IReadOnlyList<PendingSplit>? splits = null;
        try
        {
            splits = _splits?.Snapshot();
        }
        catch (Exception ex)
        {
            _log.Error("헬스 모니터: 승인 대기 조회 실패", ex);
        }

        lock (_gate)
        {
            if (roomKnown)
            {
                _room = room;
            }
            CheckRtmpHysteresis(buffer, state);
            if (freeBytes is long free)
            {
                CheckDisk(buffer, free, thresholdBytes, recordingEnabled);
            }
            if (jobs is not null)
            {
                CheckUploads(buffer, jobs);
            }
            if (splits is not null)
            {
                CheckSplits(buffer, splits);
            }
        }
        RaiseAll(buffer);
    }

    private void CheckRtmpHysteresis(List<HealthEvent> buffer, StreamState state)
    {
        if (state != StreamState.Retrying || _retryingSinceUtc is not DateTime since)
        {
            return; // recovery/clear is handled on the StateChanged edge
        }

        var elapsed = _now() - since;
        if (elapsed >= RtmpDownCriticalAfter && _rtmpDownSeverity != HealthSeverity.Critical)
        {
            _rtmpDownSeverity = HealthSeverity.Critical;
            SetCondition(buffer, HealthEventKind.RtmpDown, HealthSeverity.Critical, active: true,
                "송출 연결이 계속 실패하고 있습니다. 네트워크/스트림 키를 확인하세요.", sourceKey: null);
        }
        else if (elapsed >= RtmpDownWarnAfter && _rtmpDownSeverity is null)
        {
            _rtmpDownSeverity = HealthSeverity.Warn;
            SetCondition(buffer, HealthEventKind.RtmpDown, HealthSeverity.Warn, active: true,
                "송출 연결이 끊겨 재시도 중입니다.", sourceKey: null);
        }
    }

    private void CheckDisk(List<HealthEvent> buffer, long freeBytes, long thresholdBytes, bool recordingEnabled)
    {
        if (!recordingEnabled)
        {
            if (_diskLowSeverity is not null)
            {
                _diskLowSeverity = null;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Info, active: false,
                    "녹화가 꺼져 디스크 경고를 해제했습니다.", sourceKey: null);
            }
            return;
        }

        if (freeBytes == long.MaxValue)
        {
            return; // free-space query failed upstream — treat as unknown, don't act on it
        }

        // Two nested thresholds with a recovery margin on every UPWARD edge so severity never flaps:
        //   free < critical(=½·threshold) => Critical;  critical ≤ free < threshold => Warn;  free ≥ threshold => healthy.
        // Downward trips at the raw threshold; leaving a level upward requires clearing it by the margin. This
        // is a proper state machine so it also de-escalates Critical→Warn when space recovers into the warn band.
        var criticalBytes = thresholdBytes / 2;
        var clearBytes = thresholdBytes + DiskLowClearMarginBytes;
        var deEscalateBytes = criticalBytes + DiskLowClearMarginBytes;

        if (_diskLowSeverity is null)
        {
            if (freeBytes < criticalBytes)
            {
                _diskLowSeverity = HealthSeverity.Critical;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Critical, active: true,
                    $"녹화 디스크 여유 공간이 매우 부족합니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
            else if (freeBytes < thresholdBytes)
            {
                _diskLowSeverity = HealthSeverity.Warn;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Warn, active: true,
                    $"녹화 디스크 여유 공간이 부족합니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
        }
        else if (_diskLowSeverity == HealthSeverity.Warn)
        {
            if (freeBytes < criticalBytes)
            {
                _diskLowSeverity = HealthSeverity.Critical;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Critical, active: true,
                    $"녹화 디스크 여유 공간이 매우 부족합니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
            else if (freeBytes >= clearBytes)
            {
                _diskLowSeverity = null;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Info, active: false,
                    $"녹화 디스크 여유 공간이 회복되었습니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
        }
        else // currently Critical
        {
            if (freeBytes >= clearBytes)
            {
                _diskLowSeverity = null;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Info, active: false,
                    $"녹화 디스크 여유 공간이 회복되었습니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
            else if (freeBytes >= deEscalateBytes)
            {
                _diskLowSeverity = HealthSeverity.Warn;
                SetCondition(buffer, HealthEventKind.DiskLow, HealthSeverity.Warn, active: true,
                    $"녹화 디스크 여유 공간이 부족합니다({FormatGb(freeBytes)} 남음).", sourceKey: null);
            }
        }
    }

    private void CheckUploads(List<HealthEvent> buffer, IReadOnlyList<UploadJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (job.Status == UploadJobStatus.Failed && _reportedFailedUploadIds.Add(job.Id))
            {
                Notify(buffer, HealthEventKind.UploadFailed, HealthSeverity.Warn,
                    $"업로드에 실패했습니다: {job.Title}", job.Id);
            }
        }

        // Bound the reported-id set: the queue prunes old terminal jobs, so drop ids no longer present.
        if (_reportedFailedUploadIds.Count > 0)
        {
            var present = new HashSet<string>(jobs.Select(j => j.Id), StringComparer.Ordinal);
            _reportedFailedUploadIds.RemoveWhere(id => !present.Contains(id));
        }
    }

    /// <summary>
    /// 승인 대기 알림(승인 기반 교시 분할): pending당 1회 Info를 방출한다. 기본 NotifyLevel("warn")
    /// 에서는 텔레그램 미발송 — 경계마다 푸시를 원하는 운영자만 info로 낮춰 옵트인한다.
    /// </summary>
    private void CheckSplits(List<HealthEvent> buffer, IReadOnlyList<PendingSplit> splits)
    {
        foreach (var split in splits)
        {
            if (split.Status == PendingSplitStatus.Pending && _reportedPendingSplitIds.Add(split.Id))
            {
                var deadline = split.AutoApproveAtLocal is { } at
                    ? $"{at:HH:mm} 자동 승인" : "수동 승인 대기";
                Notify(buffer, HealthEventKind.SplitPending, HealthSeverity.Info,
                    $"{PeriodLabel.FileBase(split.Periods)} 종료 — VOD 컷 승인 대기 ({deadline})", split.Id);
            }
        }

        // Bound the reported-id set: the approval service prunes terminal splits, so follow it.
        if (_reportedPendingSplitIds.Count > 0)
        {
            var present = new HashSet<string>(splits.Select(s => s.Id), StringComparer.Ordinal);
            _reportedPendingSplitIds.RemoveWhere(id => !present.Contains(id));
        }
    }

    // ---- Emission helpers (called under _gate; raised after the lock is released) ----

    /// <summary>Records/clears an ongoing condition in <see cref="ActiveEvents"/> and queues it to raise.</summary>
    private void SetCondition(List<HealthEvent> buffer, HealthEventKind kind, HealthSeverity severity,
        bool active, string message, string? sourceKey)
    {
        var evt = new HealthEvent(kind, severity, active, message, sourceKey, _room, _now());
        var key = kind + "|" + (sourceKey ?? string.Empty);
        if (active)
        {
            _active[key] = evt;
        }
        else
        {
            _active.Remove(key);
        }
        buffer.Add(evt);
    }

    /// <summary>Queues a momentary notification (not tracked in <see cref="ActiveEvents"/>).</summary>
    private void Notify(List<HealthEvent> buffer, HealthEventKind kind, HealthSeverity severity,
        string message, string? sourceKey)
    {
        buffer.Add(new HealthEvent(kind, severity, true, message, sourceKey, _room, _now()));
    }

    private void RaiseAll(List<HealthEvent> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }
        var handler = HealthChanged;
        foreach (var evt in buffer)
        {
            LogEvent(evt);
            handler?.Invoke(this, evt);
        }
    }

    private void LogEvent(HealthEvent evt)
    {
        var line = $"[헬스:{evt.Kind}] {evt.Message}";
        switch (evt.Severity)
        {
            case HealthSeverity.Critical:
                _log.Error(line);
                break;
            case HealthSeverity.Warn:
                _log.Warn(line);
                break;
            default:
                _log.Info(line);
                break;
        }
    }

    private string ResolveMicName(string sourceId)
    {
        foreach (var source in _mixer.Sources)
        {
            if (source.Id == sourceId)
            {
                return source.Name;
            }
        }
        return "마이크";
    }

    /// <summary>
    /// Re-stamps every currently-silent mic to <paramref name="severity"/> when the live-session state flips,
    /// so a mic that fell silent before the broadcast escalates to Critical the moment it goes Live (and back to
    /// Warn on stop). Must be called under <c>_gate</c>; <see cref="SetCondition"/> overwrites the same _active key.
    /// </summary>
    private void RestampSilentMics(List<HealthEvent> buffer, HealthSeverity severity)
    {
        foreach (var sourceId in _silentMics)
        {
            SetCondition(buffer, HealthEventKind.MicSilent, severity, active: true,
                $"마이크 신호가 없습니다: {ResolveMicName(sourceId)} — 연결/음소거를 확인하세요.", sourceId);
        }
    }

    private static string FormatGb(long bytes) => (bytes / (double)GB).ToString("0.0") + "GB";

    private static string? NormalizeRoom(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                RunChecksOnce();
            }
            catch (Exception ex)
            {
                _log.Error("헬스 모니터 폴 검사 오류", ex);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _orchestrator.StateChanged -= OnStateChanged;
        _orchestrator.QualityChanged -= OnQualityChanged;
        _mixer.MicSignalChanged -= OnMicSignalChanged;
        if (_youtubeAuth is not null)
        {
            _youtubeAuth.StatusChanged -= OnYouTubeAuthStatusChanged;
        }
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed — nothing to cancel
        }
        _cts?.Dispose();
    }
}
