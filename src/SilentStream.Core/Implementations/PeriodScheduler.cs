using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Wall-clock period scheduler (확장계획서 §4.0/§5). Each loop iteration resolves the current
/// day's timetable, finds the next start/end boundary strictly after a watermark, sleeps until
/// it, and fires <see cref="PeriodStarted"/>/<see cref="PeriodEnded"/> exactly once. Periods
/// already past when <see cref="Start"/> runs are not back-fired (no recording exists for them).
/// At day rollover (or when <see cref="NotifyScheduleChanged"/> nudges it) the loop recomputes,
/// so a same-day override edit from the phone takes effect promptly.
/// </summary>
public sealed class PeriodScheduler : IPeriodScheduler
{
    private readonly IPeriodScheduleStore _store;
    private readonly ILogService _log;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _gate = new();
    private Task? _loop;
    private volatile TaskCompletionSource _changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PeriodScheduler(IPeriodScheduleStore store, ILogService log)
        : this(store, log, () => DateTime.Now, Task.Delay)
    {
    }

    /// <summary>Test seam: inject a virtual clock and delay so timing can be accelerated.</summary>
    public PeriodScheduler(
        IPeriodScheduleStore store,
        ILogService log,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _store = store;
        _log = log;
        _now = now;
        _delay = delay;
    }

    public event EventHandler<PeriodBoundary>? PeriodStarted;
    public event EventHandler<PeriodBoundary>? PeriodEnded;

    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return; // idempotent
            }
            _loop = RunAsync(ct);
        }
    }

    /// <summary>
    /// Wakes the loop early so a just-saved schedule edit is picked up without waiting for the
    /// next boundary (확장계획서 AC E4.1: 시간표 입력→스케줄러 적용).
    /// </summary>
    public void NotifyScheduleChanged() => _changed.TrySetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        // Watermark = "events at or before this instant have already been handled (or missed)".
        // Seed it with now so a mid-day launch never back-fires periods that already elapsed.
        var watermark = _now();
        _log.Info("교시 스케줄러를 시작했습니다.");

        while (!ct.IsCancellationRequested)
        {
            var now = _now();
            var today = DateOnly.FromDateTime(now);
            var next = NextEventAfter(today, watermark);

            DateTime target;
            if (next is not null)
            {
                target = next.Value.Time;
            }
            else
            {
                // No more events today — sleep until just past midnight, then recompute.
                target = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
            }

            var wait = target - _now();
            if (wait > TimeSpan.Zero && !await WaitAsync(wait, ct).ConfigureAwait(false))
            {
                return; // cancelled
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Only act if we genuinely reached the target. A NotifyScheduleChanged wake (or a
            // virtual clock that didn't advance) returns here early → the loop just recomputes.
            if (next is not null && _now() >= target)
            {
                foreach (var boundaryEvent in EventsAt(today, target))
                {
                    Fire(boundaryEvent);
                }
                watermark = target;
            }
        }
    }

    /// <summary>Earliest start/end boundary strictly after <paramref name="watermark"/> on the date.</summary>
    private BoundaryEvent? NextEventAfter(DateOnly date, DateTime watermark)
    {
        BoundaryEvent? best = null;
        foreach (var period in _store.ResolveForDate(date).Periods)
        {
            var boundary = new PeriodBoundary(
                date, period.Number,
                date.ToDateTime(period.Start),
                date.ToDateTime(period.End),
                period.ResolvedSlotId,
                period.ResolvedSlotLabel,
                period.ResolvedSlotKind,
                period.ResolvedSlotOrder);

            Consider(new BoundaryEvent(boundary.StartLocal, IsStart: true, boundary), watermark, ref best);
            Consider(new BoundaryEvent(boundary.EndLocal, IsStart: false, boundary), watermark, ref best);
        }
        return best;
    }

    /// <summary>
    /// Returns every event at one wall-clock instant. Fixed slots are contiguous, so an ending
    /// slot and the next starting slot must both fire instead of the second event being swallowed
    /// by the scheduler watermark. Ends are emitted first to finish the preceding cut before the
    /// next slot's start notification.
    /// </summary>
    private IReadOnlyList<BoundaryEvent> EventsAt(DateOnly date, DateTime instant)
    {
        var events = new List<BoundaryEvent>();
        foreach (var period in _store.ResolveForDate(date).Periods)
        {
            var boundary = new PeriodBoundary(
                date, period.Number,
                date.ToDateTime(period.Start),
                date.ToDateTime(period.End),
                period.ResolvedSlotId,
                period.ResolvedSlotLabel,
                period.ResolvedSlotKind,
                period.ResolvedSlotOrder);
            if (boundary.EndLocal == instant)
            {
                events.Add(new BoundaryEvent(instant, IsStart: false, boundary));
            }
            if (boundary.StartLocal == instant)
            {
                events.Add(new BoundaryEvent(instant, IsStart: true, boundary));
            }
        }
        return events;
    }

    private static void Consider(BoundaryEvent candidate, DateTime watermark, ref BoundaryEvent? best)
    {
        if (candidate.Time <= watermark)
        {
            return;
        }
        if (best is null || candidate.Time < best.Value.Time)
        {
            best = candidate;
        }
    }

    private void Fire(BoundaryEvent e)
    {
        if (e.IsStart)
        {
            _log.Info($"{e.Boundary.ResolvedSlotLabel} 시작 ({e.Boundary.StartLocal:HH:mm:ss}).");
            PeriodStarted?.Invoke(this, e.Boundary);
        }
        else
        {
            _log.Info($"{e.Boundary.ResolvedSlotLabel} 종료 ({e.Boundary.EndLocal:HH:mm:ss}).");
            PeriodEnded?.Invoke(this, e.Boundary);
        }
    }

    /// <summary>Waits for <paramref name="span"/> or a schedule-change nudge. Returns false on cancel.</summary>
    private async Task<bool> WaitAsync(TimeSpan span, CancellationToken ct)
    {
        var changed = _changed;
        try
        {
            var completed = await Task.WhenAny(_delay(span, ct), changed.Task).ConfigureAwait(false);
            if (completed == changed.Task)
            {
                // Reset the signal for the next wait so a single edit wakes us once.
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private readonly record struct BoundaryEvent(DateTime Time, bool IsStart, PeriodBoundary Boundary);
}
