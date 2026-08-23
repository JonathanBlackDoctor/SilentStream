using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SilentStream.Inspection.Coordinator;

internal static class LimitedProcess
{
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Process did not start.");
        using var job = Job.Create();
        job.Assign(process);
        var stdoutTask = ReadCappedAsync(process.StandardOutput, 2 * 1024 * 1024);
        var stderrTask = ReadCappedAsync(process.StandardError, 2 * 1024 * 1024);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[8192];
        var writer = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = maxChars - writer.Length;
            if (remaining > 0) writer.Append(buffer, 0, Math.Min(remaining, read));
        }
        return writer.ToString();
    }

    private sealed class Job : IDisposable
    {
        private readonly nint _handle;
        private Job(nint handle) => _handle = handle;
        public static Job Create()
        {
            var handle = CreateJobObject(nint.Zero, null);
            if (handle == nint.Zero) throw new Win32Exception();
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000 | 0x0008 | 0x0100; // kill-on-close, active-process and per-process-memory limits
            info.BasicLimitInformation.ActiveProcessLimit = 8;
            info.ProcessMemoryLimit = 512u * 1024u * 1024u;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(handle, 9, pointer, (uint)size)) throw new Win32Exception();
            }
            finally { Marshal.FreeHGlobal(pointer); }
            return new Job(handle);
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
        }
        public void Dispose() => CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize; public nuint MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
