using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Bridges the health layer to the push channels (원격 컨트롤러 개선 Phase 1): subscribes to
/// <see cref="IHealthMonitor.HealthChanged"/>, filters by the configured minimum severity
/// (config.Notifications.NotifyLevel, re-read per event so a live settings edit applies), formats a
/// Korean message, and fans it out to every registered <see cref="INotifier"/>. Push rules:
/// <list type="bullet">
///   <item>An onset pushes when it passes the filter; a RE-emit of the same condition pushes only on
///   an ESCALATION (higher severity than last pushed) — de-escalations and restamps stay silent, so
///   one long-running condition never spams (e.g. mic restamp on every live transition).</item>
///   <item>A recovery is always pushed when its onset was pushed — even below the filter level — so
///   the operator is never left staring at a stale 🚨.</item>
/// </list>
/// Dispatch is fire-and-forget with logged failures and is deliberately NOT bound to the app
/// lifetime token: the final recovery/stop pushes fire during shutdown, and the notifier's own HTTP
/// timeout bounds them, so they can neither stall shutdown nor get dropped by it. The handler body
/// never throws — it runs synchronously on health-source threads (mixer timer, orchestrator state
/// machine) where an escaped exception could kill the unattended process.
/// </summary>
public sealed class HealthNotificationService : IDisposable
{
    private readonly IHealthMonitor _healthMonitor;
    private readonly IReadOnlyList<INotifier> _notifiers;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, HealthSeverity> _notifiedConditions = new(StringComparer.Ordinal);
    private int _started;
    private int _disposed;

    public HealthNotificationService(
        IHealthMonitor healthMonitor,
        IEnumerable<INotifier> notifiers,
        IConfigStore configStore,
        ILogService log)
    {
        _healthMonitor = healthMonitor;
        _notifiers = notifiers.ToList();
        _configStore = configStore;
        _log = log;
    }

    /// <summary>Begins forwarding health events to the notifiers. Idempotent.</summary>
    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        _healthMonitor.HealthChanged += OnHealthChanged;
        _log.Info("헬스 알림 서비스를 시작했습니다.");
    }

    private void OnHealthChanged(object? sender, HealthEvent evt)
    {
        try
        {
            HandleEvent(evt);
        }
        catch (Exception ex)
        {
            // This handler runs synchronously inside HealthMonitor.RaiseAll on mixer-timer /
            // orchestrator threads — an escaped exception there can terminate the process or
            // corrupt the retry loop, so nothing may ever propagate out of here.
            _log.Error("헬스 알림 처리 오류", ex);
        }
    }

    private void HandleEvent(HealthEvent evt)
    {
        var notif = _configStore.Load().Notifications;
        var minLevel = ParseLevel(notif.NotifyLevel);

        // The bookkeeping runs even while notifications are disabled (Enabled gates only the send),
        // so toggling the master switch mid-condition can never leave a stale key that later pairs a
        // ghost ✅ with an onset the operator was never alerted to.
        bool send;
        var key = evt.Kind + "|" + (evt.SourceKey ?? string.Empty);
        lock (_gate)
        {
            if (evt.Active)
            {
                send = notif.Enabled && evt.Severity >= minLevel;
                if (send && IsCondition(evt.Kind))
                {
                    // Push a re-emit of an already-notified condition only when it escalates;
                    // de-escalations/restamps just update the recorded severity silently.
                    if (_notifiedConditions.TryGetValue(key, out var last) && evt.Severity <= last)
                    {
                        send = false;
                    }
                    _notifiedConditions[key] = evt.Severity;
                }
            }
            else
            {
                // Recovery: push iff the onset was pushed, regardless of the (Info) severity.
                send = _notifiedConditions.Remove(key) && notif.Enabled;
            }
        }
        if (!send)
        {
            return;
        }

        var message = Format(evt);
        foreach (var notifier in _notifiers)
        {
            _ = DispatchAsync(notifier, message);
        }
    }

    private async Task DispatchAsync(INotifier notifier, string message)
    {
        try
        {
            // CancellationToken.None on purpose: the shutdown sequence cancels the lifetime token
            // BEFORE StopAsync raises the final stop/recovery events, and those pushes must still go
            // out. The notifier's own timeout bounds the call, so shutdown cannot be stalled.
            await notifier.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // INotifier implementations shouldn't throw, but a broken one must not crash the pipeline.
            _log.Error("헬스 알림 전송 실패", ex);
        }
    }

    /// <summary>Maps the config string ("info"|"warn"|"critical") to a severity; anything else = warn.</summary>
    internal static HealthSeverity ParseLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "info" => HealthSeverity.Info,
        "critical" => HealthSeverity.Critical,
        _ => HealthSeverity.Warn
    };

    private static bool IsCondition(HealthEventKind kind) =>
        kind is HealthEventKind.RtmpDown or HealthEventKind.MicSilent or HealthEventKind.DiskLow
            or HealthEventKind.QualityDegraded or HealthEventKind.OauthExpiring;

    private static string Format(HealthEvent evt)
    {
        var icon = !evt.Active ? "✅" : evt.Severity switch
        {
            HealthSeverity.Critical => "🚨",
            HealthSeverity.Warn => "⚠️",
            _ => "ℹ️"
        };
        var room = string.IsNullOrWhiteSpace(evt.Room) ? string.Empty : $"[{evt.Room}] ";
        return $"{icon} {room}{evt.Message}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _healthMonitor.HealthChanged -= OnHealthChanged;
    }
}
