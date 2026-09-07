using Xunit;

namespace SilentStream.Tests;

public sealed class PowerTransitionSafetyStructureTests
{
    [Fact]
    public void Remote_shutdown_is_two_phase_cancellable_and_fail_closed()
    {
        var repository = FindRepositoryRoot();
        var server = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App", "Remote",
            "RemoteControlServer.cs"));
        var service = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App", "Remote",
            "RemoteSystemShutdownService.cs"));

        Assert.Contains("/api/system/shutdown/prepare", server, StringComparison.Ordinal);
        Assert.Contains("/api/system/shutdown/cancel", server, StringComparison.Ordinal);
        Assert.Contains("SystemShutdownConfirmRequest", server, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestShutdown()", server, StringComparison.Ordinal);
        AssertOrdered(service,
            "_safety.InspectAsync",
            "_orchestrator.StopAsync",
            "WriteState(reservedState)",
            "CreateShutdownCommand(\"/s\"");
        Assert.Contains("ConfirmationDelay", service, StringComparison.Ordinal);
        Assert.Contains("RequestCooldown", service, StringComparison.Ordinal);
        Assert.Contains("BootSessionStartedAtUtc", service, StringComparison.Ordinal);
        Assert.Contains("_durableStateAvailable", service, StringComparison.Ordinal);
        Assert.Contains("CreateShutdownCommand(\"/a\")", service, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(60)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Privileged_reboot_is_preflighted_delayed_and_rate_limited()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src",
            "SilentStream.Inspection.Broker", "BrokerWorker.cs"));

        Assert.Contains("ScheduleSafeRebootAsync(request, token)", source, StringComparison.Ordinal);
        AssertOrdered(source, "_powerSafety.InspectAsync", "WriteRebootState(reserved)",
            "RunAsync(Hidden(Path.Combine(Environment.SystemDirectory, \"shutdown.exe\")");
        Assert.Contains("_powerSafety.InspectAsync", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromHours(24)", source, StringComparison.Ordinal);
        Assert.Contains("\"/r\", \"/t\", \"60\"", source, StringComparison.Ordinal);
        Assert.Contains("TryAbortShutdownAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/r\", \"/t\", \"5\"", source, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Safety marker was not found: {marker}");
            Assert.True(current > previous, $"Safety marker is out of order: {marker}");
            previous = current;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }
}
