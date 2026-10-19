using System.Diagnostics;

namespace SilentStream.Core.Safety;

/// <summary>Waits inside the app so Windows never receives a visible shutdown countdown.</summary>
public sealed class QuietShutdownCountdown : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private bool _issued;
    public Task Completion { get; }

    public QuietShutdownCountdown(DateTimeOffset targetUtc, Func<ProcessStartInfo, Task> execute,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var clock = time ?? TimeProvider.System;
        Completion = RunAsync(targetUtc, execute, clock,
            delay ?? ((duration, token) => Task.Delay(duration, clock, token)));
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_issued) return false;
            if (!_cancel.IsCancellationRequested) _cancel.Cancel();
            return true;
        }
    }

    private async Task RunAsync(DateTimeOffset targetUtc, Func<ProcessStartInfo, Task> execute,
        TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var remaining = targetUtc - time.GetUtcNow();
        if (remaining > TimeSpan.Zero) await delay(remaining, _cancel.Token).ConfigureAwait(false);
        Task execution;
        lock (_gate)
        {
            _cancel.Token.ThrowIfCancellationRequested();
            // Do not power off unexpectedly after sleep or a stalled application.
            if (time.GetUtcNow() > targetUtc.AddSeconds(30))
                throw new InvalidOperationException("종료 시각을 놓쳐 PC를 종료하지 않았습니다.");
            _issued = true;
            var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "/s", "/f", "/t", "0", "/d", "p:4:1" })
                command.ArgumentList.Add(argument);
            execution = execute(command);
        }
        await execution.ConfigureAwait(false);
    }

    public void Dispose()
    {
        TryCancel();
        // Completion owns any outstanding registrations until it has stopped.
        _ = Completion.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default);
    }
}
