using System.Diagnostics;
using System.Text;

namespace SilentStream.Core.Media;

/// <summary>Default <see cref="IProcessRunner"/> backed by System.Diagnostics.Process.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}
