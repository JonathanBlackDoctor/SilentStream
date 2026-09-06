using SilentStream.Core.SingleInstance;
using Xunit;

namespace SilentStream.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void First_instance_is_primary_and_second_is_not()
    {
        var name = "sstream-test-" + Guid.NewGuid().ToString("N");

        using var first = new SingleInstanceGuard(name);
        Assert.True(first.IsPrimaryInstance);

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.IsPrimaryInstance);
    }

    [Fact]
    public void Releasing_the_primary_lets_a_new_instance_become_primary()
    {
        var name = "sstream-test-" + Guid.NewGuid().ToString("N");

        var first = new SingleInstanceGuard(name);
        Assert.True(first.IsPrimaryInstance);
        first.Dispose();

        using var next = new SingleInstanceGuard(name);
        Assert.True(next.IsPrimaryInstance);
    }

    [Fact]
    public void Application_acquires_single_instance_before_constructing_or_starting_the_updater()
    {
        var repository = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App", "App.xaml.cs"));
        var onStartup = Slice(appSource, "protected override void OnStartup", "private async Task StartAfterUpdateAsync");

        AssertOrdered(onStartup,
            "StartupSequence.TryAcquireSingleInstance(this, log)",
            "new AppUpdateManager(log)",
            "StartAfterUpdateAsync(log)");

        var afterUpdate = appSource[appSource.IndexOf("private async Task StartAfterUpdateAsync", StringComparison.Ordinal)..];
        AssertOrdered(afterUpdate,
            "PrepareStartupUpdateAsync()",
            "_updateManager?.Start()",
            "StartupSequence.RunAsync(_services, this)");

        var sequenceSource = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App", "Startup", "StartupSequence.cs"));
        var runAsync = Slice(sequenceSource, "public static async Task RunAsync", "private static async Task StartRemoteServerAsync");
        AssertOrdered(runAsync,
            "TryAcquireSingleInstance(app, log)",
            "new AutoStartManager(log)",
            "VodCoordinator>().Start");
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Startup marker was not found: {marker}");
            Assert.True(current > previous, $"Startup marker is out of order: {marker}");
            previous = current;
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source marker was not found: {startMarker}");
        Assert.True(end > start, $"Source marker was not found after start: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }
}
