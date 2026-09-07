namespace SilentStream.Core.Safety;

/// <summary>Raw, read-only Windows health signals captured before an OS power transition.</summary>
public sealed record SystemPowerSafetySnapshot(
    bool SystemDriveReady,
    long SystemDriveFreeBytes,
    bool SystemDiskDetected,
    bool SystemDiskOnline,
    bool SystemDiskHealthy,
    bool SystemPartitionPresent,
    bool? WindowsBootManagerReadable,
    bool StorageEventLogReadable,
    int RecentCriticalStorageEventCount,
    int RecentStorageWarningCount,
    bool InstallerBusy,
    IReadOnlyList<string> Diagnostics);

public enum SystemPowerSafetySeverity
{
    Pass,
    Warning,
    Block
}

public sealed record SystemPowerSafetyCheck(
    string Code,
    SystemPowerSafetySeverity Severity,
    string Message);

public sealed record SystemPowerSafetyAssessment(
    bool Safe,
    IReadOnlyList<SystemPowerSafetyCheck> Checks,
    IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<SystemPowerSafetyCheck> Blockers =>
        Checks.Where(check => check.Severity == SystemPowerSafetySeverity.Block).ToArray();
}

public interface ISystemPowerSafetyProbe
{
    Task<SystemPowerSafetyAssessment> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Central fail-closed policy shared by the interactive app and the privileged inspection broker.
/// The platform probe only gathers facts; this type owns every allow/block decision and remains
/// platform-neutral so the policy can be exhaustively unit tested in Linux CI.
/// </summary>
public static class SystemPowerSafetyPolicy
{
    public const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;
    public const long FreeSpaceWarningBytes = 10L * 1024 * 1024 * 1024;

    public static SystemPowerSafetyAssessment Evaluate(SystemPowerSafetySnapshot snapshot)
    {
        var checks = new List<SystemPowerSafetyCheck>();

        AddBoolean(checks, "system-drive-ready", snapshot.SystemDriveReady,
            "Windows 시스템 드라이브를 정상적으로 읽을 수 있습니다.",
            "Windows 시스템 드라이브를 정상적으로 읽을 수 없어 전원 전환을 중단합니다.");

        if (snapshot.SystemDriveReady)
        {
            var gib = snapshot.SystemDriveFreeBytes / (1024d * 1024 * 1024);
            checks.Add(snapshot.SystemDriveFreeBytes < MinimumFreeBytes
                ? new("system-drive-free-space", SystemPowerSafetySeverity.Block,
                    $"시스템 드라이브 여유 공간이 {gib:F1} GiB로 5 GiB 미만입니다.")
                : snapshot.SystemDriveFreeBytes < FreeSpaceWarningBytes
                    ? new("system-drive-free-space", SystemPowerSafetySeverity.Warning,
                        $"시스템 드라이브 여유 공간이 {gib:F1} GiB로 적습니다.")
                    : new("system-drive-free-space", SystemPowerSafetySeverity.Pass,
                        $"시스템 드라이브 여유 공간은 {gib:F1} GiB입니다."));
        }

        AddBoolean(checks, "system-disk-detected", snapshot.SystemDiskDetected,
            "Windows가 설치된 물리 디스크를 감지했습니다.",
            "Windows가 설치된 물리 디스크를 식별하지 못해 전원 전환을 중단합니다.");
        AddBoolean(checks, "system-disk-online", snapshot.SystemDiskOnline,
            "Windows 시스템 디스크가 온라인 상태입니다.",
            "Windows 시스템 디스크가 오프라인이거나 상태를 확인할 수 없습니다.");
        AddBoolean(checks, "system-disk-health", snapshot.SystemDiskHealthy,
            "Windows 시스템 디스크 상태가 정상입니다.",
            "Windows 시스템 디스크 상태가 정상이 아니거나 확인되지 않았습니다.");
        AddBoolean(checks, "system-partition", snapshot.SystemPartitionPresent,
            "시스템 부팅 파티션을 확인했습니다.",
            "시스템 부팅 파티션을 확인하지 못해 전원 전환을 중단합니다.");
        checks.Add(snapshot.WindowsBootManagerReadable switch
        {
            true => new("windows-boot-manager", SystemPowerSafetySeverity.Pass,
                "Windows Boot Manager 구성을 읽었습니다."),
            false => new("windows-boot-manager", SystemPowerSafetySeverity.Block,
                "Windows Boot Manager 구성을 읽지 못해 전원 전환을 중단합니다."),
            null => new("windows-boot-manager", SystemPowerSafetySeverity.Warning,
                "현재 권한으로 Windows Boot Manager를 직접 확인할 수 없어 디스크와 시스템 파티션 검사 결과를 사용합니다.")
        });
        AddBoolean(checks, "storage-event-log", snapshot.StorageEventLogReadable,
            "최근 저장장치 오류 기록을 확인했습니다.",
            "최근 저장장치 오류 기록을 확인할 수 없어 전원 전환을 중단합니다.");

        checks.Add(snapshot.RecentCriticalStorageEventCount > 0
            ? new("critical-storage-events", SystemPowerSafetySeverity.Block,
                $"최근 24시간에 중대한 저장장치 오류가 {snapshot.RecentCriticalStorageEventCount}건 기록되었습니다.")
            : new("critical-storage-events", SystemPowerSafetySeverity.Pass,
                "최근 24시간에 중대한 저장장치 오류가 없습니다."));
        if (snapshot.RecentStorageWarningCount > 0)
        {
            checks.Add(new("storage-warnings", SystemPowerSafetySeverity.Warning,
                $"최근 24시간에 저장장치 재시도·경고가 {snapshot.RecentStorageWarningCount}건 기록되었습니다."));
        }

        checks.Add(snapshot.InstallerBusy
            ? new("installer-busy", SystemPowerSafetySeverity.Block,
                "Windows Installer가 작업 중이어서 전원 전환을 중단합니다.")
            : new("installer-busy", SystemPowerSafetySeverity.Pass,
                "진행 중인 Windows Installer 작업이 없습니다."));

        return new SystemPowerSafetyAssessment(
            checks.All(check => check.Severity != SystemPowerSafetySeverity.Block),
            checks,
            snapshot.Diagnostics);
    }

    private static void AddBoolean(List<SystemPowerSafetyCheck> checks, string code, bool passed,
        string success, string failure) => checks.Add(new SystemPowerSafetyCheck(code,
        passed ? SystemPowerSafetySeverity.Pass : SystemPowerSafetySeverity.Block,
        passed ? success : failure));
}
