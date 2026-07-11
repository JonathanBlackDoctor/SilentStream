using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// HealthNotificationService tests: severity filtering, onset/recovery pairing, multi-notifier
/// fan-out, and failure isolation. Dispatch is fire-and-forget, so assertions poll with
/// WaitUntilAsync (VodCoordinatorTests precedent).
/// </summary>
public class HealthNotificationServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-notify-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeHealthMonitor _monitor = new();
    private readonly FakeNotifier _notifier = new();

    public HealthNotificationServiceTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Notifications.Enabled = true;
        config.Notifications.NotifyLevel = "warn";
        _configStore.Save(config);
    }

    private HealthNotificationService CreateStarted(params INotifier[] extraNotifiers)
    {
        var notifiers = new List<INotifier> { _notifier };
        notifiers.AddRange(extraNotifiers);
        var service = new HealthNotificationService(_monitor, notifiers, _configStore, new LogService());
        service.Start(CancellationToken.None);
        return service;
    }

    private static HealthEvent Event(
        HealthEventKind kind, HealthSeverity severity, bool active = true,
        string message = "메시지", string? sourceKey = null, string? room = null) =>
        new(kind, severity, active, message, sourceKey, room, DateTime.UtcNow);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Event_below_the_min_level_is_filtered()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.LiveStarted, HealthSeverity.Info));
        await Task.Delay(150);

        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Event_at_the_min_level_is_dispatched_with_icon_and_message()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn, message: "마이크 신호가 없습니다"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        var sent = Assert.Single(_notifier.Sent);
        Assert.Contains("⚠️", sent);
        Assert.Contains("마이크 신호가 없습니다", sent);
    }

    [Fact]
    public async Task Critical_event_reaches_every_notifier()
    {
        var second = new FakeNotifier();
        using var service = CreateStarted(second);

        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Critical));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1 && second.Sent.Count >= 1);

        Assert.Contains("🚨", Assert.Single(_notifier.Sent));
        Assert.Single(second.Sent);
    }

    [Fact]
    public async Task Disabled_config_sends_nothing()
    {
        _configStore.Update(c => c.Notifications.Enabled = false);
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Critical));
        await Task.Delay(150);

        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Recovery_of_a_notified_condition_is_sent_even_below_the_level()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn, sourceKey: "mic:1"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        // Recovery arrives as Info/Active=false — below the warn filter, but its onset was pushed,
        // so the pairing rule must push it too (never leave the operator staring at a stale 🚨).
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Info, active: false,
            message: "마이크 신호가 복구되었습니다", sourceKey: "mic:1"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 2);

        Assert.Contains("✅", _notifier.Sent[1]);
        Assert.Contains("복구", _notifier.Sent[1]);
    }

    [Fact]
    public async Task Recovery_of_an_unnotified_condition_is_dropped()
    {
        _configStore.Update(c => c.Notifications.NotifyLevel = "critical");
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn, sourceKey: "mic:1")); // filtered
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Info, active: false, sourceKey: "mic:1"));
        await Task.Delay(150);

        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Escalation_of_a_notified_condition_sends_again()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.DiskLow, HealthSeverity.Warn));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        _monitor.Raise(Event(HealthEventKind.DiskLow, HealthSeverity.Critical)); // 5초 폴이 승격 재방출
        await WaitUntilAsync(() => _notifier.Sent.Count >= 2);

        Assert.Contains("🚨", _notifier.Sent[1]);
    }

    [Fact]
    public async Task De_escalation_and_same_severity_restamps_do_not_spam()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Critical, sourceKey: "mic:1"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        // 라이브 종료 시 재스탬프(Critical→Warn)와 동일 심각도 재방출은 새 정보가 아니다 — 무음 유지.
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn, sourceKey: "mic:1"));
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn, sourceKey: "mic:1"));
        await Task.Delay(150);
        Assert.Single(_notifier.Sent);

        // 강등 뒤 재승격은 다시 알린다.
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Critical, sourceKey: "mic:1"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 2);

        // 회복은 여전히 짝지어 전송된다.
        _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Info, active: false, sourceKey: "mic:1"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 3);
        Assert.Contains("✅", _notifier.Sent[2]);
    }

    [Fact]
    public async Task Recovery_while_disabled_consumes_bookkeeping_without_ghost_push()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Warn));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        _configStore.Update(c => c.Notifications.Enabled = false);
        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Info, active: false)); // 회복(비활성 중)
        await Task.Delay(150);
        Assert.Single(_notifier.Sent); // 비활성 중엔 발송 없음

        // 재활성 후 새 에피소드가 필터에 걸려도(critical 레벨) 유령 ✅가 나오면 안 된다.
        _configStore.Update(c =>
        {
            c.Notifications.Enabled = true;
            c.Notifications.NotifyLevel = "critical";
        });
        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Warn));               // 필터됨
        _monitor.Raise(Event(HealthEventKind.RtmpDown, HealthSeverity.Info, active: false)); // 짝 없는 회복
        await Task.Delay(150);

        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task A_throwing_config_store_does_not_crash_the_health_thread()
    {
        var service = new HealthNotificationService(
            _monitor, [_notifier], new ThrowingConfigStore(), new LogService());
        service.Start(CancellationToken.None);
        using var _ = service;

        // Raise는 헬스 스레드의 동기 호출을 흉내낸다 — 예외가 새어 나오면 이 호출이 throw한다.
        var ex = Record.Exception(() =>
            _monitor.Raise(Event(HealthEventKind.MicSilent, HealthSeverity.Warn)));

        Assert.Null(ex);
        await Task.Delay(50);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task A_throwing_notifier_does_not_block_the_others()
    {
        var broken = new FakeNotifier { Throw = true };
        using var service = CreateStarted(broken);

        _monitor.Raise(Event(HealthEventKind.DiskLow, HealthSeverity.Warn));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        Assert.Single(_notifier.Sent); // healthy notifier delivered despite the broken sibling
    }

    [Fact]
    public async Task Room_label_is_prefixed_to_the_message()
    {
        using var service = CreateStarted();

        _monitor.Raise(Event(HealthEventKind.DiskLow, HealthSeverity.Warn, room: "201호"));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);

        Assert.Contains("[201호]", Assert.Single(_notifier.Sent));
    }

    [Fact]
    public async Task Start_is_idempotent_one_send_per_event()
    {
        using var service = CreateStarted();
        service.Start(CancellationToken.None); // second Start must not double-subscribe

        _monitor.Raise(Event(HealthEventKind.UploadFailed, HealthSeverity.Warn));
        await WaitUntilAsync(() => _notifier.Sent.Count >= 1);
        await Task.Delay(150); // grace so a duplicate would have landed

        Assert.Single(_notifier.Sent);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "조건이 제한 시간 안에 충족되지 않았습니다.");
    }

    // ---- fakes ----

    private sealed class ThrowingConfigStore : IConfigStore
    {
        public AppConfig Load() => throw new IOException("파일 잠김");
        public void Save(AppConfig config) => throw new IOException("파일 잠김");
        public void Update(Action<AppConfig> mutate) => throw new IOException("파일 잠김");
    }

    private sealed class FakeHealthMonitor : IHealthMonitor
    {
        public event EventHandler<HealthEvent>? HealthChanged;
        public IReadOnlyList<HealthEvent> ActiveEvents => [];
        public void Start(CancellationToken ct) { }
        public void Dispose() { }
        public void Raise(HealthEvent evt) => HealthChanged?.Invoke(this, evt);
    }

    private sealed class FakeNotifier : INotifier
    {
        private readonly List<string> _sent = [];
        public bool Throw;

        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) { return _sent.ToList(); } }
        }

        public Task<bool> SendAsync(string message, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("고장난 채널");
            }
            lock (_sent) { _sent.Add(message); }
            return Task.FromResult(true);
        }
    }
}
