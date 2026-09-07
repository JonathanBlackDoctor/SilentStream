using SilentStream.Platform.Windows;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class WindowsSystemPowerSafetyProbeTests
{
    [Fact]
    public async Task Read_only_probe_returns_every_required_signal_without_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var assessment = await new WindowsSystemPowerSafetyProbe().InspectAsync()
            .WaitAsync(TimeSpan.FromSeconds(20));

        var codes = assessment.Checks.Select(check => check.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("system-drive-ready", codes);
        Assert.Contains("system-disk-detected", codes);
        Assert.Contains("system-disk-online", codes);
        Assert.Contains("system-disk-health", codes);
        Assert.Contains("system-partition", codes);
        Assert.Contains("windows-boot-manager", codes);
        Assert.Contains("storage-event-log", codes);
        Assert.Contains("critical-storage-events", codes);
        Assert.Contains("installer-busy", codes);
    }
}
