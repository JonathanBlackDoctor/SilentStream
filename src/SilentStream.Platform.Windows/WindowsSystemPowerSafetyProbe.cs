using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using SilentStream.Core.Safety;

namespace SilentStream.Platform.Windows;

/// <summary>
/// Performs only read-only checks against Windows storage, BCD and event-log surfaces. Any probe
/// failure is represented as an unknown/unsafe signal; this class never repairs or mutates boot
/// configuration.
/// </summary>
public sealed class WindowsSystemPowerSafetyProbe : ISystemPowerSafetyProbe
{
    private static readonly TimeSpan EventLookback = TimeSpan.FromHours(24);

    public async Task<SystemPowerSafetyAssessment> InspectAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        var driveReady = false;
        var freeBytes = 0L;
        try
        {
            var drive = new DriveInfo(systemDrive);
            driveReady = drive.IsReady;
            if (driveReady) freeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add($"system-drive:{ex.GetType().Name}");
        }

        var storage = ReadStorageState(systemDrive, diagnostics);
        var bootManagerReadable = await CanReadBootManagerAsync(diagnostics, cancellationToken).ConfigureAwait(false);
        var events = ReadStorageEvents(diagnostics);
        var installerBusy = IsWindowsInstallerBusy(diagnostics);
        var snapshot = new SystemPowerSafetySnapshot(
            driveReady,
            freeBytes,
            storage.DiskDetected,
            storage.DiskOnline,
            storage.DiskHealthy,
            storage.SystemPartitionPresent,
            bootManagerReadable,
            events.Readable,
            events.CriticalCount,
            events.WarningCount,
            installerBusy,
            diagnostics);
        return SystemPowerSafetyPolicy.Evaluate(snapshot);
    }

    private static StorageState ReadStorageState(string systemDrive, ICollection<string> diagnostics)
    {
        try
        {
            var letter = systemDrive.Length > 0 ? char.ToUpperInvariant(systemDrive[0]) : '\0';
            var targetDisks = new HashSet<uint>();
            var systemPartitionDisks = new HashSet<uint>();
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using (var partitions = new ManagementObjectSearcher(scope,
                       new ObjectQuery("SELECT DiskNumber, DriveLetter, IsBoot, IsSystem FROM MSFT_Partition")))
            using (var results = partitions.Get())
            {
                foreach (ManagementObject partition in results)
                {
                    using (partition)
                    {
                        var isBoot = BooleanValue(partition["IsBoot"]);
                        var isSystem = BooleanValue(partition["IsSystem"]);
                        var driveLetter = CharacterValue(partition["DriveLetter"]);
                        if (partition["DiskNumber"] is null) continue;
                        var diskNumber = Convert.ToUInt32(partition["DiskNumber"]);
                        if (isSystem) systemPartitionDisks.Add(diskNumber);
                        if (isBoot || driveLetter == letter) targetDisks.Add(diskNumber);
                    }
                }
            }

            var detected = false;
            var online = true;
            var healthy = true;
            using (var disks = new ManagementObjectSearcher(scope,
                       new ObjectQuery("SELECT Number, FriendlyName, HealthStatus, IsOffline FROM MSFT_Disk")))
            using (var results = disks.Get())
            {
                foreach (ManagementObject disk in results)
                {
                    using (disk)
                    {
                        if (disk["Number"] is null || !targetDisks.Contains(Convert.ToUInt32(disk["Number"]))) continue;
                        detected = true;
                        online &= !BooleanValue(disk["IsOffline"]);
                        healthy &= disk["HealthStatus"] is not null && Convert.ToUInt16(disk["HealthStatus"]) == 0;
                    }
                }
            }

            if (targetDisks.Count == 0) diagnostics.Add("storage:no-system-disk-mapping");
            var systemPartitionPresent = targetDisks.Overlaps(systemPartitionDisks);
            return new StorageState(detected, detected && online, detected && healthy, systemPartitionPresent);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException
                                           or FormatException or OverflowException or COMException)
        {
            diagnostics.Add($"storage:{ex.GetType().Name}");
            return new StorageState(false, false, false, false);
        }
    }

    private static async Task<bool?> CanReadBootManagerAsync(ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/enum");
            start.ArgumentList.Add("{bootmgr}");
            using var process = Process.Start(start);
            if (process is null)
            {
                diagnostics.Add("boot-manager:process-not-started");
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                diagnostics.Add("boot-manager:timeout");
                return false;
            }

            if (process.ExitCode == 0) return true;
            if (!IsAdministrator())
            {
                diagnostics.Add($"boot-manager:requires-elevation-exit-{process.ExitCode}");
                return null;
            }
            diagnostics.Add($"boot-manager:exit-{process.ExitCode}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or Win32Exception or SecurityException)
        {
            diagnostics.Add($"boot-manager:{ex.GetType().Name}");
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static StorageEvents ReadStorageEvents(ICollection<string> diagnostics)
    {
        try
        {
            var lookbackMs = (long)EventLookback.TotalMilliseconds;
            var providers = "(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='stornvme'] " +
                            "or Provider[@Name='storahci'] or Provider[@Name='Microsoft-Windows-WHEA-Logger'])";
            var critical = CountEvents(providers,
                "(EventID=7 or EventID=11 or EventID=15 or EventID=18 or EventID=55 or EventID=157)", lookbackMs);
            var warnings = CountEvents(providers,
                "(EventID=17 or EventID=19 or EventID=51 or EventID=129 or EventID=153)", lookbackMs);
            return new StorageEvents(true, critical, warnings);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Add($"storage-events:{ex.GetType().Name}");
            return new StorageEvents(false, 0, 0);
        }
    }

    private static int CountEvents(string providers, string eventIds, long lookbackMs)
    {
        var xpath = $"*[System[{providers} and {eventIds} and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";
        var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        var count = 0;
        while (count < 100)
        {
            using var item = reader.ReadEvent();
            if (item is null) break;
            count++;
        }
        return count;
    }

    private static bool IsWindowsInstallerBusy(ICollection<string> diagnostics)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(@"Global\_MSIExecute");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add("installer:mutex-access-denied");
            return true;
        }
    }

    private static bool BooleanValue(object? value) => value is not null && Convert.ToBoolean(value);

    private static char CharacterValue(object? value)
    {
        if (value is null) return '\0';
        if (value is char character) return char.ToUpperInvariant(character);
        var text = Convert.ToString(value);
        return string.IsNullOrEmpty(text) ? '\0' : char.ToUpperInvariant(text[0]);
    }

    private sealed record StorageState(bool DiskDetected, bool DiskOnline, bool DiskHealthy,
        bool SystemPartitionPresent);

    private sealed record StorageEvents(bool Readable, int CriticalCount, int WarningCount);
}
