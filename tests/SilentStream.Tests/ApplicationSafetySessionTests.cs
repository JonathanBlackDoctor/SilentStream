using SilentStream.Core.Safety;
using Xunit;

namespace SilentStream.Tests;

public sealed class ApplicationSafetySessionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-run-intent-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private ApplicationSafetySession Begin(string boot = "boot-one", bool automatic = true)
    {
        var session = new ApplicationSafetySession(_directory, boot);
        Assert.True(session.Begin(12, 123, automatic, Now));
        return session;
    }

    [Theory]
    [InlineData(DesiredRunMode.Stopped)]
    [InlineData(DesiredRunMode.RecordingOnly)]
    public void Explicit_mode_survives_same_boot_app_restarts_and_updates(DesiredRunMode mode)
    {
        var session = Begin();
        Assert.True(session.SetDesired(mode, "operator"));
        Assert.NotNull(session.BeginTransition(ApplicationTransition.Update, "update"));
        Assert.True(session.MarkCleanExit());
        Assert.Equal(mode, Begin().DesiredMode);
    }

    [Fact]
    public void Manual_stop_blocks_crash_recovery_even_with_a_recent_heartbeat()
    {
        var session = Begin();
        Assert.True(session.Heartbeat(true, Now));
        Assert.True(session.SetDesired(DesiredRunMode.Stopped, "operator-stop"));
        Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(10)));
    }

    [Theory]
    [InlineData(ApplicationTransition.PowerShutdown)]
    [InlineData(ApplicationTransition.Update)]
    [InlineData(ApplicationTransition.Uninstall)]
    [InlineData(ApplicationTransition.Restart)]
    public void Maintenance_has_one_owner_and_blocks_start_and_competing_recovery(ApplicationTransition transition)
    {
        var session = Begin();
        session.Heartbeat(true, Now);
        var handoff = Assert.IsType<ApplicationRunState>(session.BeginTransition(transition, "requested"));
        Assert.False(session.StartAllowed);
        Assert.Null(session.BeginTransition(ApplicationTransition.Restart, "competing"));
        Assert.False(session.SetDesired(DesiredRunMode.Live, "competing-start"));
        Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(10)));
        Assert.True(session.CancelTransition(handoff.TransitionToken!));
        Assert.True(session.StartAllowed);
        Assert.Equal(DesiredRunMode.Live, session.DesiredMode);
    }

    [Fact]
    public void Restart_handoff_survives_normal_exit_but_can_only_be_consumed_once()
    {
        var session = Begin();
        session.SetDesired(DesiredRunMode.RecordingOnly, "broadcast-stop");
        var handoff = session.BeginTransition(ApplicationTransition.Restart, "restart")!;
        session.MarkCleanExit();
        Assert.True(session.ConsumeRestart(session.RunId!, handoff.TransitionToken!, handoff.Revision));
        Assert.False(session.ConsumeRestart(session.RunId!, handoff.TransitionToken!, handoff.Revision));
        Assert.Equal(DesiredRunMode.RecordingOnly, Begin().DesiredMode);
    }

    [Fact]
    public void Later_manual_stop_revokes_old_restart_ticket_and_old_generation_cannot_write()
    {
        var session = Begin();
        var handoff = session.BeginTransition(ApplicationTransition.Restart, "restart")!;
        session.SetDesired(DesiredRunMode.Stopped, "later-stop");
        Assert.False(session.ConsumeRestart(session.RunId!, handoff.TransitionToken!, handoff.Revision));
        var replacement = Begin();
        Assert.False(session.MarkCleanExit());
        Assert.False(session.Heartbeat(true, Now));
        Assert.Equal(replacement.RunId, replacement.Snapshot!.RunId);
    }

    [Fact]
    public void Hold_survives_reboot_and_corrupt_marker_and_revokes_pending_restart()
    {
        var session = Begin();
        var handoff = session.BeginTransition(ApplicationTransition.Restart, "restart")!;
        File.WriteAllText(ApplicationSafetySession.HoldFile(_directory), "{damaged");
        Assert.True(session.PreservationHeld);
        Assert.False(session.ConsumeRestart(session.RunId!, handoff.TransitionToken!, handoff.Revision));
        var rebooted = Begin("new-boot");
        Assert.False(rebooted.StartAllowed);
        Assert.Equal(DesiredRunMode.Stopped, rebooted.DesiredMode);
    }

    [Fact]
    public void Corrupt_intent_is_not_replaced_by_an_automatic_live_default()
    {
        var session = Begin();
        File.WriteAllText(session.StatePath, "{broken");
        Assert.False(new ApplicationSafetySession(_directory, "new-boot").Begin(13, 124, true, Now));
        Assert.Equal("{broken", File.ReadAllText(session.StatePath));
    }

    [Fact]
    public void Transition_is_not_granted_when_callback_succeeds_but_atomic_write_fails()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.Delete is enforced by the Windows application host.
        var session = Begin();
        var failures = 0;
        session.PersistenceFailed += () => failures++;
        using var denyReplacement = new FileStream(session.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Null(session.BeginTransition(ApplicationTransition.Update, "must-persist-first"));
        Assert.Equal(ApplicationTransition.None, session.Snapshot!.Transition);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void Same_boot_interrupted_power_countdown_stays_stopped_but_new_boot_follows_autostart()
    {
        var session = Begin();
        session.BeginTransition(ApplicationTransition.PowerShutdown, "scheduled");
        Assert.Equal(DesiredRunMode.Stopped, Begin().DesiredMode);
        Assert.Equal(DesiredRunMode.Live, Begin("new-boot").DesiredMode);
    }

    [Fact]
    public void Recovery_is_generation_bound_single_claim_and_rate_limited_across_restarts()
    {
        var session = Begin();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            session.Heartbeat(true, Now);
            Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 999, Now.AddSeconds(10)));
            Assert.NotNull(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(10)));
            Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(11)));
            session = Begin();
        }
        session.Heartbeat(true, Now);
        Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(10)));
        session.Heartbeat(true, Now.AddMinutes(31));
        Assert.NotNull(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddMinutes(31).AddSeconds(1)));
    }

    [Fact]
    public void Stale_or_future_heartbeat_cannot_authorise_recovery()
    {
        var session = Begin();
        session.Heartbeat(true, Now);
        Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddMinutes(3)));
        Assert.Null(session.ClaimCrashRecovery(session.RunId!, 12, 123, Now.AddSeconds(-1)));
    }

    [Fact]
    public void Maintenance_lease_excludes_app_launch_and_active_app_excludes_maintenance()
    {
        using (var app = ApplicationSafetySession.AcquireExecutionLease(_directory, false))
        {
            Assert.NotNull(app);
            Assert.Null(ApplicationSafetySession.AcquireExecutionLease(_directory, true));
        }
        using var maintenance = ApplicationSafetySession.AcquireExecutionLease(_directory, true);
        Assert.NotNull(maintenance);
        Assert.Null(ApplicationSafetySession.AcquireExecutionLease(_directory, false));
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
