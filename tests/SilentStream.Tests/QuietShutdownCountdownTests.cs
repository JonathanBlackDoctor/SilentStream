using System.Diagnostics;
using SilentStream.Core.Safety;
using Xunit;

namespace SilentStream.Tests;

public sealed class QuietShutdownCountdownTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Waits_in_app_then_issues_hidden_zero_timeout_without_comment()
    {
        var clock = new Clock();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessStartInfo? command = null;
        using var countdown = new QuietShutdownCountdown(clock.Now.AddMinutes(2),
            value => { command = value; return Task.CompletedTask; }, clock,
            (duration, token) => { Assert.Equal(TimeSpan.FromMinutes(2), duration); return ready.Task.WaitAsync(token); });
        Assert.Null(command);
        clock.Now = clock.Now.AddMinutes(2);
        ready.SetResult();
        await countdown.Completion;
        Assert.NotNull(command);
        Assert.True(command.CreateNoWindow);
        Assert.False(command.UseShellExecute);
        Assert.Equal(new[] { "/s", "/f", "/t", "0", "/d", "p:4:1" }, command.ArgumentList);
        Assert.False(countdown.TryCancel());
    }

    [Fact]
    public async Task Cancellation_and_disposal_prevent_dispatch_even_when_delay_completes()
    {
        foreach (var dispose in new[] { false, true })
        {
            var clock = new Clock();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var issued = 0;
            using var countdown = new QuietShutdownCountdown(clock.Now.AddMinutes(2),
                _ => { issued++; return Task.CompletedTask; }, clock, (_, _) => ready.Task);
            if (dispose) countdown.Dispose(); else Assert.True(countdown.TryCancel());
            ready.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => countdown.Completion);
            Assert.Equal(0, issued);
        }
    }

    [Fact]
    public async Task Sleep_past_target_never_triggers_a_late_shutdown()
    {
        var clock = new Clock();
        var issued = false;
        using var countdown = new QuietShutdownCountdown(clock.Now.AddMinutes(2),
            _ => { issued = true; return Task.CompletedTask; }, clock,
            (_, _) => { clock.Now = clock.Now.AddHours(1); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => countdown.Completion);
        Assert.False(issued);
    }

    [Fact]
    public async Task Dispatch_failure_is_reported_and_never_retried()
    {
        var clock = new Clock();
        var calls = 0;
        using var countdown = new QuietShutdownCountdown(clock.Now, _ =>
        {
            calls++;
            throw new IOException("mock command failure");
        }, clock);
        await Assert.ThrowsAsync<IOException>(() => countdown.Completion);
        Assert.Equal(1, calls);
        Assert.False(countdown.TryCancel());
    }
}
