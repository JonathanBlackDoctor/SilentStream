using System.Management;

namespace SilentStream.Platform.Windows;

public static class WindowsBootIdentity
{
    public static string Read()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using var results = search.Get();
            var raw = results.Cast<ManagementObject>().Select(x => x["LastBootUpTime"]?.ToString()).FirstOrDefault();
            return string.IsNullOrWhiteSpace(raw) ? string.Empty
                : ManagementDateTimeConverter.ToDateTime(raw).ToUniversalTime().ToString("O");
        }
        catch { return string.Empty; } // Unknown boot cannot authorise an unattended restart.
    }
}
