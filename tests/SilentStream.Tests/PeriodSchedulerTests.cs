using SilentStream.Core.Implementations;
using Xunit;

namespace SilentStream.Tests;

public class PeriodSchedulerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-sched-").FullName;
    private readonly PeriodScheduleStore _store;
    private readonly List<string> _started = [];
    private readonly List<string> _ended = [];

    public PeriodSchedulerTests()
    {
        var configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        configStore.Save(SilentStream.Core.Models.AppConfig.CreateDefault());
        _store = new PeriodScheduleStore();
    }

    private sealed class VirtualClock(DateTime start)
    {
        private readonly object _lock = new();
        private DateTime _now = start;

        public DateTime Now { get { lock (_lock) { return _now; } } }

        public async Task Delay(TimeSpan span, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (span > TimeSpan.FromHours(6))
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return;
            }
            lock (_lock) { _now = _now.Add(span); }
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Fires_all_nine_fixed_slots_once_including_lunch()
    {
        var date = new DateOnly(2026, 6, 15); // Monday
        var clock = new VirtualClock(date.ToDateTime(new TimeOnly(8, 24)));
        var scheduler = new PeriodScheduler(_store, new LogService(), () => clock.Now, clock.Delay);
        scheduler.PeriodStarted += (_, b) => { lock (_started) { _started.Add(b.ResolvedSlotId); } };
        scheduler.PeriodEnded += (_, b) => { lock (_ended) { _ended.Add(b.ResolvedSlotId); } };

        using var cts = new CancellationTokenSource();
        scheduler.Start(cts.Token);
        await WaitUntilAsync(() => Count(_ended) >= 9, TimeSpan.FromSeconds(5));
        cts.Cancel();

        string[] expected =
        [
            "period-1", "period-2", "period-3", "period-4", "lunch",
            "period-5", "period-6", "period-7", "period-8"
        ];
        lock (_started) { Assert.Equal(expected, _started); }
        lock (_ended) { Assert.Equal(expected, _ended); }
    }

    [Fact]
    public async Task Slots_already_past_at_start_are_not_back_fired()
    {
        var date = new DateOnly(2026, 6, 15); // Monday
        var clock = new VirtualClock(date.ToDateTime(new TimeOnly(14, 25)));
        var scheduler = new PeriodScheduler(_store, new LogService(), () => clock.Now, clock.Delay);
        scheduler.PeriodStarted += (_, b) => { lock (_started) { _started.Add(b.ResolvedSlotId); } };
        scheduler.PeriodEnded += (_, b) => { lock (_ended) { _ended.Add(b.ResolvedSlotId); } };

        using var cts = new CancellationTokenSource();
        scheduler.Start(cts.Token);
        await WaitUntilAsync(() => Count(_ended) >= 3, TimeSpan.FromSeconds(5));
        cts.Cancel();

        lock (_started) { Assert.Equal(["period-7", "period-8"], _started); }
        lock (_ended) { Assert.Equal(["period-6", "period-7", "period-8"], _ended); }
    }

    private int Count(List<string> list) { lock (list) { return list.Count; } }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
