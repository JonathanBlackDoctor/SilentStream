using SilentStream.Core.Safety;
using Xunit;

namespace SilentStream.Tests;

public sealed class SystemPowerSafetyTests
{
    [Fact]
    public void Healthy_snapshot_allows_a_power_transition()
    {
        var result = SystemPowerSafetyPolicy.Evaluate(Healthy());

        Assert.True(result.Safe);
        Assert.Empty(result.Blockers);
    }

    [Theory]
    [InlineData("drive")]
    [InlineData("disk-detected")]
    [InlineData("disk-online")]
    [InlineData("disk-health")]
    [InlineData("system-partition")]
    [InlineData("event-log")]
    [InlineData("critical-event")]
    [InlineData("installer")]
    public void Unsafe_boot_signal_blocks_a_power_transition(string signal)
    {
        var snapshot = signal switch
        {
            "drive" => Healthy() with { SystemDriveReady = false },
            "disk-detected" => Healthy() with { SystemDiskDetected = false },
            "disk-online" => Healthy() with { SystemDiskOnline = false },
            "disk-health" => Healthy() with { SystemDiskHealthy = false },
            "system-partition" => Healthy() with { SystemPartitionPresent = false },
            "event-log" => Healthy() with { StorageEventLogReadable = false },
            "critical-event" => Healthy() with { RecentCriticalStorageEventCount = 1 },
            "installer" => Healthy() with { InstallerBusy = true },
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };

        var result = SystemPowerSafetyPolicy.Evaluate(snapshot);

        Assert.False(result.Safe);
        Assert.NotEmpty(result.Blockers);
    }

    [Fact]
    public void Unreadable_boot_manager_blocks_but_permission_limited_check_warns()
    {
        var unreadable = SystemPowerSafetyPolicy.Evaluate(Healthy() with
        {
            WindowsBootManagerReadable = false
        });
        var permissionLimited = SystemPowerSafetyPolicy.Evaluate(Healthy() with
        {
            WindowsBootManagerReadable = null
        });

        Assert.False(unreadable.Safe);
        Assert.True(permissionLimited.Safe);
        Assert.Contains(permissionLimited.Checks,
            check => check.Code == "windows-boot-manager"
                     && check.Severity == SystemPowerSafetySeverity.Warning);
    }

    [Fact]
    public void Low_free_space_blocks_but_storage_retry_warnings_do_not()
    {
        var lowSpace = SystemPowerSafetyPolicy.Evaluate(Healthy() with
        {
            SystemDriveFreeBytes = SystemPowerSafetyPolicy.MinimumFreeBytes - 1
        });
        var warningsOnly = SystemPowerSafetyPolicy.Evaluate(Healthy() with
        {
            RecentStorageWarningCount = 3
        });

        Assert.False(lowSpace.Safe);
        Assert.True(warningsOnly.Safe);
        Assert.Contains(warningsOnly.Checks,
            check => check.Severity == SystemPowerSafetySeverity.Warning);
    }

    private static SystemPowerSafetySnapshot Healthy() => new(
        SystemDriveReady: true,
        SystemDriveFreeBytes: 20L * 1024 * 1024 * 1024,
        SystemDiskDetected: true,
        SystemDiskOnline: true,
        SystemDiskHealthy: true,
        SystemPartitionPresent: true,
        WindowsBootManagerReadable: true,
        StorageEventLogReadable: true,
        RecentCriticalStorageEventCount: 0,
        RecentStorageWarningCount: 0,
        InstallerBusy: false,
        Diagnostics: []);
}
