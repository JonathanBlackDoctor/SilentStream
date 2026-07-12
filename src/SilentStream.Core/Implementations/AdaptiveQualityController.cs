using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// The adaptive-quality policy (확장계획서_적응형송출품질 §3/§5). Consumes windowed metrics
/// ticks, steps the per-session quality ladder down on sustained encode overload (windowed fps
/// deficit) or uplink congestion (tee fifo drops), and steps back up conservatively — after a
/// healthy dwell, preferring 쉬는 시간 (no class period running) so a working broadcast is never
/// glitched mid-class. It never touches the pipeline: it raises <see cref="ChangeRequested"/> and
/// the orchestrator swaps. All state sits behind one gate; events are raised outside it. The
/// injected clock is LOCAL time (period checks compare against the timetable); metric samples are
/// stamped with it on arrival so every dwell/cooldown is unit-testable without real time
/// (HealthMonitor seam pattern).
/// </summary>
public sealed class AdaptiveQualityController : IAdaptiveQualityController
{
    // Policy constants (§5.2/§5.3) — code constants like HealthMonitor's windows, not config.
    private static readonly TimeSpan MetricsWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleAfterStart = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepDownCooldown = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecoverDwell = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthyWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FlapLockout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NetDropSpread = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ChangeCapWindow = TimeSpan.FromHours(1);
    private const double EncodeDeficitRatio = 0.85;
    private const double EncodeDeficitTickShare = 0.8;
    private const double HealthyFpsRatio = 0.95;
    private const int MinWindowSamples = 10;
    private const int MinNetDropTicks = 3;
    private const int MaxChangesPerHour = 8;

    private static readonly string[] LevelNames = ["원본", "절약 1단계", "절약 2단계", "안전 모드"];

    private readonly IConfigStore _configStore;
    private readonly IPeriodScheduleStore _schedule;
    private readonly ILogService _log;
    private readonly Func<DateTime> _now;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = [];
    private readonly List<DateTime> _autoChangeTimes = [];
    private readonly Dictionary<int, DateTime> _levelLockedUntil = [];

    private IReadOnlyList<QualityStep> _ladder = [];
    private (int W, int H, double Fps, int Kbps)? _ladderBase;
    private QualityMode _mode = QualityMode.Auto;
    private int _level;
    private QualityChangeReason _lastReason = QualityChangeReason.None;
    private DateTime? _degradedSince;
    private StreamState _state = StreamState.Idle;
    private DateTime _settleStartedAt = DateTime.MinValue;
    private DateTime _lastChangeAt = DateTime.MinValue;
    private DateTime _lastUnhealthyAt = DateTime.MinValue;
    private bool _lastChangeWasUp;
    private bool _capSuppressionLogged;
    private int _lastDropCount;

    private readonly record struct Sample(DateTime At, double Fps, bool DropsIncreased);

    /// <summary>Production constructor (DI selects this — the clock seam is not registered).</summary>
    public AdaptiveQualityController(
        IConfigStore configStore, IPeriodScheduleStore schedule, ILogService log)
        : this(configStore, schedule, log, () => DateTime.Now)
    {
    }

    /// <summary>Test seam: inject a virtual LOCAL clock so dwell/cooldown runs without real time.</summary>
    public AdaptiveQualityController(
        IConfigStore configStore, IPeriodScheduleStore schedule, ILogService log, Func<DateTime> now)
    {
        _configStore = configStore;
        _schedule = schedule;
        _log = log;
        _now = now;
    }

    public QualityMode Mode { get { lock (_gate) { return _mode; } } }

    public int Level { get { lock (_gate) { return _level; } } }

    public IReadOnlyList<QualityStep> Ladder { get { lock (_gate) { return _ladder; } } }

    public QualityStatus Status { get { lock (_gate) { return BuildStatusLocked(_lastReason); } } }

    public event EventHandler<QualityStatus>? ChangeRequested;

    public EncoderStartOptions Apply(EncoderStartOptions baseOptions)
    {
        lock (_gate)
        {
            RebuildLadderIfBaseChangedLocked(baseOptions);

            // Every encoder (re)start begins a fresh settle window: the new process's counters
            // (and possibly a new level) need time before triggers may fire again.
            _settleStartedAt = _now();
            _lastUnhealthyAt = _now();
            _samples.Clear();
            _lastDropCount = 0; // a fresh ffmpeg restarts its cumulative drop counter

            _level = Math.Clamp(_level, 0, _ladder.Count - 1);
            if (_level == 0)
            {
                return baseOptions;
            }
            var step = _ladder[_level];
            return baseOptions with
            {
                VideoBitrateKbps = step.VideoBitrateKbps,
                OutputWidth = step.Width,
                OutputHeight = step.Height,
                OutputFps = step.Fps
            };
        }
    }

    public void OnMetrics(MetricsSnapshot metrics)
    {
        bool live;
        lock (_gate)
        {
            live = _state == StreamState.Live && _ladder.Count > 0;
        }
        if (!live)
        {
            return;
        }
        // Config read outside the gate (file I/O; this handler runs on the encoder's stderr thread).
        var adaptive = _configStore.Load().Encoding.Adaptive;

        QualityStatus? decision;
        lock (_gate)
        {
            if (_state != StreamState.Live || _ladder.Count == 0)
            {
                return;
            }
            var now = _now();

            // Sample bookkeeping runs regardless of mode/enabled so the window is warm the moment
            // automatic control (re)engages.
            var dropsIncreased = metrics.TeeDropCount > _lastDropCount;
            _lastDropCount = metrics.TeeDropCount;
            _samples.Add(new Sample(now, metrics.Fps, dropsIncreased));
            _samples.RemoveAll(s => now - s.At > MetricsWindow);

            var targetFps = _ladder[Math.Clamp(_level, 0, _ladder.Count - 1)].Fps;
            if (metrics.Fps < targetFps * HealthyFpsRatio || dropsIncreased)
            {
                _lastUnhealthyAt = now;
            }

            if (_mode == QualityMode.ManualHold || !adaptive.Enabled ||
                now - _settleStartedAt < SettleAfterStart)
            {
                return;
            }
            decision = EvaluateLocked(adaptive, targetFps, now);
        }
        if (decision is not null)
        {
            ChangeRequested?.Invoke(this, decision);
        }
    }

    public void OnStateChanged(StreamState state)
    {
        QualityStatus? reset = null;
        lock (_gate)
        {
            _state = state;
            if (state != StreamState.Idle)
            {
                return;
            }
            // Session over: manual holds are session-scoped (D-AQ4) and the level returns to
            // 원본, so tomorrow's unattended start is always clean.
            _levelLockedUntil.Clear();
            _autoChangeTimes.Clear();
            _samples.Clear();
            _capSuppressionLogged = false;
            _lastChangeWasUp = false;
            if (_level != 0 || _mode != QualityMode.Auto)
            {
                _mode = QualityMode.Auto;
                _level = 0;
                _degradedSince = null;
                _lastChangeAt = DateTime.MinValue;
                _lastReason = QualityChangeReason.SessionReset;
                reset = BuildStatusLocked(QualityChangeReason.SessionReset);
            }
        }
        if (reset is not null)
        {
            _log.Info("세션 종료 — 송출 품질을 원본/자동으로 초기화했습니다.");
            ChangeRequested?.Invoke(this, reset);
        }
    }

    public void SetManual(int level)
    {
        QualityStatus status;
        lock (_gate)
        {
            var max = _ladder.Count > 0 ? _ladder.Count - 1 : LevelNames.Length - 1;
            _mode = QualityMode.ManualHold;
            _level = Math.Clamp(level, 0, max);
            _lastChangeAt = _now();
            _degradedSince = _level > 0 ? _degradedSince ?? _now() : null;
            _lastReason = QualityChangeReason.ManualSet;
            _samples.Clear();
            status = BuildStatusLocked(QualityChangeReason.ManualSet);
        }
        _log.Info($"송출 품질 수동 고정: {status.LevelName} (세션 한정 — 전체 중지 시 자동으로 복귀)");
        ChangeRequested?.Invoke(this, status);
    }

    public void SetAuto()
    {
        QualityStatus? status = null;
        lock (_gate)
        {
            if (_mode != QualityMode.Auto)
            {
                _mode = QualityMode.Auto;
                // Fresh dwell + health accounting so the controller doesn't bounce the level the
                // instant the operator hands control back.
                _lastChangeAt = _now();
                _lastUnhealthyAt = _now();
                _lastReason = QualityChangeReason.ManualSet;
                status = BuildStatusLocked(QualityChangeReason.ManualSet);
            }
        }
        if (status is not null)
        {
            _log.Info("송출 품질을 자동 조절로 되돌렸습니다(수동 고정 해제).");
            ChangeRequested?.Invoke(this, status);
        }
    }

    // ---- decision core (all *Locked methods run under _gate) ----

    private QualityStatus? EvaluateLocked(AdaptiveConfig adaptive, double targetFps, DateTime now)
    {
        var maxAuto = Math.Min(adaptive.MaxLevel, _ladder.Count - 1);

        // Step DOWN — cooldown-gated so each new level gets time to prove itself (and each change
        // costs an encoder swap). NET is checked before ENC inside the detector: congestion is the
        // commonest field failure and the encode fps looks perfectly healthy while it happens.
        if (_level < maxAuto && now - _lastChangeAt >= StepDownCooldown)
        {
            var reason = DetectDowngradeLocked(targetFps);
            if (reason != QualityChangeReason.None)
            {
                return StepToLocked(_level + 1, reason, now);
            }
        }

        // Step UP — conservative recovery (§5.3): long dwell, a fully healthy window, no flap
        // lockout on the target level, and never while a class period is running (an up-swap
        // glitches a WORKING broadcast for 2~4s, so it waits for 쉬는 시간; an empty timetable
        // imposes no gate).
        if (_level > 0 && adaptive.AutoRecover
            && now - _lastChangeAt >= RecoverDwell
            && now - _lastUnhealthyAt >= HealthyWindow
            && !(_levelLockedUntil.TryGetValue(_level - 1, out var lockedUntil) && now < lockedUntil)
            && !IsPeriodActiveNow(now))
        {
            return StepToLocked(_level - 1, QualityChangeReason.AutoRecover, now);
        }
        return null;
    }

    private QualityChangeReason DetectDowngradeLocked(double targetFps)
    {
        if (_samples.Count < MinWindowSamples)
        {
            return QualityChangeReason.None;
        }

        // NET: fifo drops observed on several ticks SPREAD across the window — one bad moment
        // (a single burst tick) must not trigger; sustained congestion must (§5.2).
        var firstDrop = default(DateTime?);
        var lastDrop = default(DateTime?);
        var dropTicks = 0;
        foreach (var sample in _samples)
        {
            if (!sample.DropsIncreased)
            {
                continue;
            }
            dropTicks++;
            firstDrop ??= sample.At;
            lastDrop = sample.At;
        }
        if (dropTicks >= MinNetDropTicks && lastDrop - firstDrop >= NetDropSpread)
        {
            return QualityChangeReason.NetworkCongestion;
        }

        // ENC: windowed fps persistently below target — mean AND per-tick share together, so a
        // brief dip inside an otherwise healthy window doesn't trip it.
        var deficitLine = targetFps * EncodeDeficitRatio;
        var mean = _samples.Average(s => s.Fps);
        var share = _samples.Count(s => s.Fps < deficitLine) / (double)_samples.Count;
        if (mean < deficitLine && share >= EncodeDeficitTickShare)
        {
            return QualityChangeReason.EncodeOverload;
        }
        return QualityChangeReason.None;
    }

    private QualityStatus? StepToLocked(int newLevel, QualityChangeReason reason, DateTime now)
    {
        // Safety cap: pathological flapping must not restart the encoder all day (§5.3).
        _autoChangeTimes.RemoveAll(t => now - t >= ChangeCapWindow);
        if (_autoChangeTimes.Count >= MaxChangesPerHour)
        {
            if (!_capSuppressionLogged)
            {
                _capSuppressionLogged = true;
                _log.Warn($"품질 자동 조절이 시간당 상한({MaxChangesPerHour}회)에 도달해 보류됩니다 — 반복 원인을 점검하세요.");
            }
            return null;
        }
        _capSuppressionLogged = false;

        if (newLevel > _level && _lastChangeWasUp && now - _lastChangeAt < FlapWindow)
        {
            // The level we just tried to return to can't hold — burn it for a while (봉인).
            _levelLockedUntil[_level] = now + FlapLockout;
            _log.Info($"품질 단계 봉인: {LevelNameAtLocked(_level)} — 복귀 직후 재하강(플랩)으로 {FlapLockout.TotalMinutes:F0}분 보류합니다.");
        }
        _lastChangeWasUp = newLevel < _level;
        _level = newLevel;
        _lastChangeAt = now;
        _lastUnhealthyAt = now; // health accounting restarts at the new level
        _autoChangeTimes.Add(now);
        _degradedSince = newLevel > 0 ? _degradedSince ?? now : null;
        _lastReason = reason;
        _samples.Clear();

        var status = BuildStatusLocked(reason);
        _log.Info($"품질 자동 조절 결정: {status.LevelName}" +
                  (status.Applied is { } s ? $" ({s.Width}x{s.Height}@{s.Fps:0.#}fps {s.VideoBitrateKbps}kbps)" : string.Empty));
        return status;
    }

    private QualityStatus BuildStatusLocked(QualityChangeReason reason)
    {
        var applied = _ladder.Count > 0 ? _ladder[Math.Clamp(_level, 0, _ladder.Count - 1)] : null;
        var name = applied?.Name ?? LevelNames[Math.Clamp(_level, 0, LevelNames.Length - 1)];
        return new QualityStatus(_mode, _level, name, reason, applied, _degradedSince);
    }

    private string LevelNameAtLocked(int level) =>
        level < _ladder.Count ? _ladder[level].Name : LevelNames[Math.Clamp(level, 0, LevelNames.Length - 1)];

    private bool IsPeriodActiveNow(DateTime now)
    {
        try
        {
            var time = TimeOnly.FromDateTime(now);
            return _schedule.ResolveForDate(DateOnly.FromDateTime(now))
                .Periods.Any(p => p.Start <= time && time < p.End);
        }
        catch (Exception ex)
        {
            _log.Debug($"교시 판정 실패(복귀를 보류하지 않고 진행): {ex.Message}");
            return false;
        }
    }

    // ---- ladder ----

    private void RebuildLadderIfBaseChangedLocked(EncoderStartOptions baseOptions)
    {
        var width = baseOptions.OutputWidth > 0 ? baseOptions.OutputWidth : baseOptions.Width;
        var height = baseOptions.OutputHeight > 0 ? baseOptions.OutputHeight : baseOptions.Height;
        var fps = baseOptions.OutputFps > 0 ? baseOptions.OutputFps : baseOptions.Fps;
        var key = (width, height, fps, baseOptions.VideoBitrateKbps);
        if (_ladderBase == key)
        {
            return;
        }
        _ladderBase = key;
        _ladder = BuildLadder(width, height, fps, baseOptions.VideoBitrateKbps);
        _log.Info("품질 사다리 구성: " + string.Join(" / ", _ladder.Select(s =>
            $"{s.Name} {s.Width}x{s.Height}@{s.Fps:0.#}fps {s.VideoBitrateKbps}kbps")));
    }

    /// <summary>
    /// Builds the per-session ladder from the resolved base parameters (§4). For lecture screen
    /// content the resolution (text legibility) is preserved longest, fps drops mid-way (slides
    /// don't need 60), bitrate is the first lever; resolution changes are the last resort partly
    /// because YouTube's player re-syncs on them. Rungs that save less than 10% over the previous
    /// one at the same shape are skipped (degenerate ladder). Audio is never degraded.
    /// </summary>
    public static IReadOnlyList<QualityStep> BuildLadder(int width, int height, double fps, int videoKbps)
    {
        var steps = new List<QualityStep> { new(0, LevelNames[0], width, height, fps, videoKbps) };
        void Add(string name, int w, int h, double f, int kbps)
        {
            var prev = steps[^1];
            var sameShape = w == prev.Width && h == prev.Height && Math.Abs(f - prev.Fps) < 0.01;
            if (sameShape && kbps > prev.VideoBitrateKbps * 0.9)
            {
                return;
            }
            steps.Add(new QualityStep(steps.Count, name, w, h, f, kbps));
        }

        Add(LevelNames[1], width, height, fps, (int)Math.Round(videoKbps * 0.6));
        var reducedFps = Math.Min(30, fps);
        Add(LevelNames[2], width, height, reducedFps, (int)Math.Round(videoKbps * 0.4));
        var (safeWidth, safeHeight) = FitInto(width, height, 1280, 720);
        Add(LevelNames[3], safeWidth, safeHeight, reducedFps, Math.Min(2500, (int)Math.Round(videoKbps * 0.4)));
        return steps;
    }

    /// <summary>Aspect-preserving downscale into the box (never upscales), even dimensions.</summary>
    public static (int Width, int Height) FitInto(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= maxWidth && height <= maxHeight)
        {
            return (width - width % 2, height - height % 2);
        }
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        var w = (int)Math.Round(width * scale);
        var h = (int)Math.Round(height * scale);
        return (w - w % 2, h - h % 2);
    }
}
