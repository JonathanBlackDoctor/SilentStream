namespace SilentStream.Core.Remote;

/// <summary>
/// Access rules for settings changed from the phone remote. The mode is persisted in
/// <c>RemoteConfig.SettingsLockMode</c>; this class keeps the UI and HTTP handlers on the same
/// small, fail-closed vocabulary.
/// </summary>
public static class RemoteSettingsLock
{
    public const string Unlocked = "unlocked";
    public const string ScheduleOnly = "scheduleOnly";
    public const string Locked = "locked";

    /// <summary>Normalizes persisted data. A missing legacy field is unlocked; an unknown value is locked.</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return Unlocked;
        }

        return TryParse(mode, out var normalized) ? normalized : Locked;
    }

    /// <summary>Validates a value submitted by the remote UI without silently accepting typos.</summary>
    public static bool TryParse(string? mode, out string normalized)
    {
        if (string.Equals(mode, Unlocked, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Unlocked;
            return true;
        }
        if (string.Equals(mode, ScheduleOnly, StringComparison.OrdinalIgnoreCase))
        {
            normalized = ScheduleOnly;
            return true;
        }
        if (string.Equals(mode, Locked, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Locked;
            return true;
        }

        normalized = Locked;
        return false;
    }

    public static bool Allows(string? mode, RemoteSettingsSection section) =>
        section == RemoteSettingsSection.Schedule
            ? Normalize(mode) != Locked
            : Normalize(mode) == Unlocked;

    public static string DenialMessage(RemoteSettingsSection section) =>
        section == RemoteSettingsSection.Schedule
            ? "설정이 잠겨 있어 시간표를 변경할 수 없습니다. 설정 잠금을 해제한 뒤 다시 시도하세요."
            : "설정이 잠겨 있어 변경할 수 없습니다. '시간표만 수정 가능' 모드에서는 시간표만 바꿀 수 있습니다.";
}

public enum RemoteSettingsSection
{
    Schedule,
    Other
}
