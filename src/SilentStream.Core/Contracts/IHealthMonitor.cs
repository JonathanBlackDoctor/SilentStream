using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// The health/event layer (원격 컨트롤러 개선 로드맵 Phase 0). Subscribes to the orchestrator, audio
/// mixer, recording manager and upload queue and translates their scattered signals into a single
/// typed stream of <see cref="HealthEvent"/>s with severity and debounce/hysteresis, so it never
/// spams. It is the shared foundation the later phases consume: Phase 1 폰 푸시(텔레그램/PWA),
/// Phase 2 멀티 호실 상태, and the control-UI/status surfaces all subscribe to <see cref="HealthChanged"/>.
/// </summary>
public interface IHealthMonitor : IDisposable
{
    /// <summary>
    /// Raised on every emitted health event (onset, recovery and momentary notifications). Handlers
    /// run on a background thread — the monitor never marshals to the UI thread.
    /// </summary>
    event EventHandler<HealthEvent> HealthChanged;

    /// <summary>
    /// Snapshot of the currently-active conditions (rtmp down, silent mics, disk low). Momentary
    /// events (live started/stopped, upload failed) are not retained here. Safe to read from any thread.
    /// </summary>
    IReadOnlyList<HealthEvent> ActiveEvents { get; }

    /// <summary>
    /// Begins observing (subscribes to source events and starts the periodic poll for the no-event
    /// signals). Idempotent; the poll loop stops on <paramref name="ct"/> or <see cref="IDisposable.Dispose"/>.
    /// </summary>
    void Start(CancellationToken ct);
}
