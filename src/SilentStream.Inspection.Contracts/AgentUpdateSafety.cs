using System.Diagnostics;
using System.Text.Json;

namespace SilentStream.Inspection;

public static class AgentUpdateSafety
{
    public static string CurrentAppDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaCaptureHelper");

    public static FileStream? AcquireApplicationLease(string appDirectory, bool maintenance)
    {
        try
        {
            if (!Path.IsPathFullyQualified(appDirectory)) return null;
            try
            {
                using var hold = File.Open(Path.Combine(appDirectory, "original-preservation.hold.json"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return null;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            Directory.CreateDirectory(appDirectory);
            return new FileStream(Path.Combine(appDirectory, "app-execution.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, maintenance ? FileShare.None : FileShare.ReadWrite);
        }
        catch { return null; }
    }

    public static FileStream? AcquireMaintenanceFromCoordinator(string coordinatorDirectory)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(coordinatorDirectory, "coordinator.json")));
            if (!config.RootElement.TryGetProperty("appConfigPath", out var path) &&
                !config.RootElement.TryGetProperty("AppConfigPath", out path)) return null;
            var file = path.GetString();
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) ||
                !string.Equals(Path.GetFileName(file), "config.json", StringComparison.OrdinalIgnoreCase)) return null;
            return AcquireApplicationLease(Path.GetDirectoryName(file)!, maintenance: true);
        }
        catch { return null; }
    }

    /// <summary>Fail closed: a helper update must not terminate a running recording app.</summary>
    public static bool ApplicationIsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("MediaCaptureHelper");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return true; }
    }
}
