using System.Diagnostics;
using System.IO;
using SilentStream.Core.Contracts;

namespace SilentStream.App.Remote;

/// <summary>
/// Queues a deliberate Windows shutdown requested from a paired phone. The HTTP endpoint returns
/// first; the recording and broadcast are then stopped gracefully before Windows is told to power
/// off. Only one shutdown can be queued at a time.
/// </summary>
public sealed class RemoteSystemShutdownService(IStreamOrchestrator orchestrator, ILogService log)
{
    private int _shutdownQueued;

    public bool RequestShutdown()
    {
        if (Interlocked.CompareExchange(ref _shutdownQueued, 1, 0) != 0)
        {
            return false;
        }

        _ = ShutdownAfterResponseAsync();
        return true;
    }

    private async Task ShutdownAfterResponseAsync()
    {
        // Give Kestrel time to return the accepted response to the phone before its network goes away.
        await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);

        try
        {
            await orchestrator.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed graceful finalisation must not strand an explicitly requested shutdown.
            log.Error("원격 PC 종료 전 송출·녹화 종료 중 오류", ex);
        }

        try
        {
            var shutdownExe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            var command = new ProcessStartInfo
            {
                FileName = File.Exists(shutdownExe) ? shutdownExe : "shutdown.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            command.ArgumentList.Add("/s");
            command.ArgumentList.Add("/t");
            command.ArgumentList.Add("0");

            if (Process.Start(command) is null)
            {
                throw new InvalidOperationException("Windows 종료 명령을 시작하지 못했습니다.");
            }

            log.Warn("원격 요청으로 PC 종료를 시작합니다.");
        }
        catch (Exception ex)
        {
            log.Error("원격 PC 종료 요청 실패", ex);
            Interlocked.Exchange(ref _shutdownQueued, 0);
        }
    }
}
