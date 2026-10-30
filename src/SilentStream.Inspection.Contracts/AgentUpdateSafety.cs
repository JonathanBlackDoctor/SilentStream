using System.Diagnostics;

namespace SilentStream.Inspection;

public static class AgentUpdateSafety
{
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
