using System.Diagnostics;

namespace SilentStream.UninstallHelper;

public static class ApplicationRestart
{
    public interface IParentProcess : IDisposable
    {
        long StartTimeUtcTicks { get; }
        Task WaitForExitAsync(CancellationToken token);
    }

    public static Task<int> RestartAfterExitAsync(int processId, long startedAtUtcTicks, string executable) =>
        RestartAfterExitAsync(processId, startedAtUtcTicks, executable, OpenParent, path =>
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
            return process is not null;
        }, TimeSpan.FromSeconds(90));

    public static async Task<int> RestartAfterExitAsync(int processId, long startedAtUtcTicks, string executable,
        Func<int, IParentProcess?> openParent, Func<string, bool> launch, TimeSpan timeout)
    {
        if (processId <= 0 || startedAtUtcTicks <= 0 || !Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "MediaCaptureHelper.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable)) return 10;
        try
        {
            using var parent = openParent(processId);
            if (parent is not null)
            {
                // A reused PID is not proof that the expected application exited safely.
                if (parent.StartTimeUtcTicks != startedAtUtcTicks) return 11;
                using var deadline = new CancellationTokenSource(timeout);
                await parent.WaitForExitAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            return launch(executable) ? 0 : 12;
        }
        catch (OperationCanceledException) { return 13; }
        catch (Exception) { return 14; }
    }

    private static IParentProcess? OpenParent(int id)
    {
        try { return new ParentProcess(Process.GetProcessById(id)); }
        catch (ArgumentException) { return null; }
    }

    private sealed class ParentProcess(Process process) : IParentProcess
    {
        public long StartTimeUtcTicks => process.StartTime.ToUniversalTime().Ticks;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void Dispose() => process.Dispose();
    }
}
