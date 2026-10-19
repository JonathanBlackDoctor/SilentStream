using SilentStream.Core.Safety;
using Xunit;

namespace SilentStream.Tests;

public sealed class ShutdownScheduleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "silentstream-schedule-" + Guid.NewGuid());
    private readonly Clock _clock = new();
    private string FilePath => Path.Combine(_directory, "schedule.json");
    private ShutdownScheduleManager Create() => new(FilePath, _clock);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(43201)]
    public void Rejects_past_too_soon_and_over_30_days(int minutes)
    {
        var manager = Create();
        Assert.False(manager.Schedule(_clock.Now.AddMinutes(minutes), "phone").Ok);
        Assert.Null(manager.State);
    }

    [Fact]
    public async Task Persists_target_as_utc_survives_restart_and_runs_once_in_preparation_window()
    {
        var target = _clock.Now.ToOffset(TimeSpan.FromHours(9)).AddMinutes(10);
        Assert.True(Create().Schedule(target, "phone").Ok);
        var restored = Create();
        Assert.Equal(TimeSpan.Zero, restored.State!.TargetUtc.Offset);
        Assert.Equal(target, restored.State.TargetUtc);
        var calls = 0;
        Task<ShutdownScheduleResult> Execute(ShutdownScheduleState state)
        {
            calls++;
            Assert.Equal("phone", state.ActorId);
            Assert.Equal("executing", System.Text.Json.JsonSerializer.Deserialize<ShutdownScheduleState>(File.ReadAllText(FilePath))!.Status);
            return Task.FromResult(new ShutdownScheduleResult(true, "Windows 대기", target));
        }
        await restored.PollAsync(Execute);
        Assert.Equal(0, calls);
        _clock.Now = target.AddMinutes(-2);
        await restored.PollAsync(Execute);
        await restored.PollAsync(Execute);
        Assert.Equal(1, calls);
        Assert.Equal("scheduled", Create().State!.Status);
    }

    [Fact]
    public async Task Missed_target_does_not_shutdown_late()
    {
        var manager = Create();
        manager.Schedule(_clock.Now.AddMinutes(5), "phone");
        _clock.Now = _clock.Now.AddMinutes(5);
        await manager.PollAsync(_ => throw new Exception("must not execute"));
        Assert.Equal("missed", manager.State!.Status);
        Assert.False(manager.Active);
    }

    [Fact]
    public async Task Cancel_pending_is_durable_and_never_calls_windows()
    {
        var manager = Create();
        manager.Schedule(_clock.Now.AddMinutes(5), "phone");
        Assert.True((await manager.CancelAsync(manager.State!.Id, () => throw new Exception("must not abort OS"))).Ok);
        _clock.Now = _clock.Now.AddMinutes(4);
        await manager.PollAsync(_ => throw new Exception("must not execute"));
        Assert.Equal("cancelled", Create().State!.Status);
    }

    [Fact]
    public async Task Replacement_requires_cancellation_and_stale_cancel_cannot_remove_new_reservation()
    {
        var manager = Create();
        manager.Schedule(_clock.Now.AddMinutes(5), "phone");
        var oldId = manager.State!.Id;
        Assert.False(manager.Schedule(_clock.Now.AddMinutes(10), "phone").Ok);
        await manager.CancelAsync(oldId, () => throw new Exception());
        Assert.True(manager.Schedule(_clock.Now.AddMinutes(10), "phone").Ok);
        Assert.False((await manager.CancelAsync(oldId, () => throw new Exception())).Ok);
        Assert.Equal("pending", manager.State!.Status);
    }

    [Fact]
    public async Task Failed_safety_or_finalization_is_terminal_without_retry()
    {
        var manager = Create();
        manager.Schedule(_clock.Now.AddMinutes(5), "phone");
        _clock.Now = _clock.Now.AddMinutes(3);
        await manager.PollAsync(_ => Task.FromResult(new ShutdownScheduleResult(false, "안전 점검 차단")));
        await manager.PollAsync(_ => throw new Exception("must not retry"));
        Assert.Equal("failed", Create().State!.Status);
        Assert.Contains("안전 점검", manager.State!.Message);
    }

    [Fact]
    public async Task Cancel_during_finalization_waits_then_aborts_windows_before_reporting_success()
    {
        var manager = Create();
        var target = _clock.Now.AddMinutes(5);
        manager.Schedule(target, "phone");
        _clock.Now = target.AddMinutes(-2);
        var finishing = new TaskCompletionSource<ShutdownScheduleResult>();
        var execution = manager.PollAsync(_ => finishing.Task);
        var aborted = false;
        var cancel = manager.CancelAsync(manager.State!.Id, () =>
        {
            aborted = true;
            return Task.FromResult(new ShutdownScheduleResult(true, "취소됨"));
        });
        Assert.False(cancel.IsCompleted);
        finishing.SetResult(new(true, "Windows 대기", target));
        await execution;
        Assert.True((await cancel).Ok);
        Assert.True(aborted);
        Assert.Equal("cancelled", Create().State!.Status);
    }

    [Fact]
    public async Task Failed_windows_abort_keeps_pending_shutdown_visible()
    {
        var manager = Create();
        var target = _clock.Now.AddMinutes(5);
        manager.Schedule(target, "phone");
        _clock.Now = target.AddMinutes(-2);
        await manager.PollAsync(_ => Task.FromResult(new ShutdownScheduleResult(true, "대기", target)));
        var result = await manager.CancelAsync(manager.State!.Id,
            () => Task.FromResult(new ShutdownScheduleResult(false, "취소 실패")));
        Assert.False(result.Ok);
        Assert.True(manager.Active);
        Assert.Equal("scheduled", Create().State!.Status);
    }

    [Fact]
    public async Task Crash_after_claim_is_not_replayed()
    {
        var manager = Create();
        manager.Schedule(_clock.Now.AddMinutes(5), "phone");
        _clock.Now = _clock.Now.AddMinutes(3);
        ShutdownScheduleManager? restored = null;
        await manager.PollAsync(_ =>
        {
            restored = Create();
            return Task.FromResult(new ShutdownScheduleResult(false, "중단"));
        });
        Assert.Equal("failed", restored!.State!.Status);
        await restored.PollAsync(_ => throw new Exception("must not replay"));
    }

    [Fact]
    public async Task Observers_receive_scheduled_status_without_waiting_for_stream_metrics()
    {
        var manager = Create();
        var states = new List<string>();
        manager.Changed += () => states.Add(manager.State!.Status);
        var target = _clock.Now.AddMinutes(5);
        manager.Schedule(target, "phone");
        _clock.Now = target.AddMinutes(-2);
        await manager.PollAsync(_ => Task.FromResult(new ShutdownScheduleResult(true, "대기", target)));
        Assert.Equal(new[] { "pending", "executing", "scheduled" }, states);
    }

    [Fact]
    public async Task Reboot_does_not_replay_a_previously_armed_windows_countdown()
    {
        var manager = Create();
        var target = _clock.Now.AddMinutes(5);
        manager.Schedule(target, "phone");
        _clock.Now = target.AddMinutes(-2);
        await manager.PollAsync(_ => Task.FromResult(new ShutdownScheduleResult(true, "대기", target)));
        var restored = new ShutdownScheduleManager(FilePath, _clock, nativeShutdownPending: false);
        Assert.Equal("failed", restored.State!.Status);
        Assert.False(restored.Active);
        await restored.PollAsync(_ => throw new Exception("must not replay after reboot"));
    }

    [Fact]
    public void Corrupt_or_unwritable_storage_never_accepts_a_schedule()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "invalid");
        Assert.False(Create().Schedule(_clock.Now.AddMinutes(5), "phone").Ok);
        var blocked = new ShutdownScheduleManager(Path.Combine(FilePath, "child.json"), _clock);
        Assert.False(blocked.Schedule(_clock.Now.AddMinutes(5), "phone").Ok);
    }

    [Fact]
    public async Task Countdown_failure_is_visible_without_overwriting_a_different_or_cancelled_reservation()
    {
        var manager = Create();
        var target = _clock.Now.AddMinutes(5);
        manager.Schedule(target, "phone");
        _clock.Now = target.AddMinutes(-2);
        await manager.PollAsync(_ => Task.FromResult(new ShutdownScheduleResult(true, "앱 대기", target)));
        await manager.ReportFailureAsync(target.AddMinutes(1), "다른 예약");
        Assert.Equal("scheduled", manager.State!.Status);
        await manager.ReportFailureAsync(target, "실행 실패");
        Assert.Equal("failed", Create().State!.Status);
        Assert.False(manager.Active);
        await manager.CancelAsync(manager.State!.Id, () => Task.FromResult(new ShutdownScheduleResult(true, "취소됨")));
        await manager.ReportFailureAsync(target, "늦은 실패 응답");
        Assert.Equal("cancelled", manager.State!.Status);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
