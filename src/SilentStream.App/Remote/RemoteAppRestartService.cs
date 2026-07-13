using System.Diagnostics;
using System.IO;
using System.Windows;
using SilentStream.Core.Contracts;

namespace SilentStream.App.Remote;

/// <summary>
/// Performs a remote-requested application restart without leaving ffmpeg or the embedded server
/// behind. The replacement process is launched by a tiny detached Windows command after this
/// process exits, so the single-instance guard never races the old instance.
/// </summary>
public sealed class RemoteAppRestartService(IStreamOrchestrator orchestrator, ILogService log)
{
    private int _restartQueued;

    /// <summary>Queues one graceful restart and returns false when one is already pending.</summary>
    public bool RequestRestart()
    {
        if (Interlocked.CompareExchange(ref _restartQueued, 1, 0) != 0)
        {
            return false;
        }

        _ = RestartAfterResponseAsync();
        return true;
    }

    private async Task RestartAfterResponseAsync()
    {
        // Let Kestrel flush the 202 response before its own shutdown closes the connection.
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            log.Error("원격 앱 재시작 실패: 실행 파일 경로를 찾지 못했습니다.");
            Interlocked.Exchange(ref _restartQueued, 0);
            return;
        }

        try
        {
            await orchestrator.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Continue with the process restart: the new host and existing cleanup hooks can
            // still recover a partially stopped session, but make the failure visible in diagnostics.
            log.Error("원격 앱 재시작 전 송출 종료 중 오류", ex);
        }

        try
        {
            var command = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            command.ArgumentList.Add("/d");
            command.ArgumentList.Add("/c");
            command.ArgumentList.Add($"timeout /t 2 /nobreak > nul & start \"\" \"{executable}\"");
            if (Process.Start(command) is null)
            {
                throw new InvalidOperationException("재시작 도우미 프로세스를 시작하지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            log.Error("원격 앱 재시작 실패: 새 인스턴스를 예약하지 못했습니다.", ex);
            Interlocked.Exchange(ref _restartQueued, 0);
            return;
        }

        log.Info("원격 요청으로 앱을 다시 시작합니다.");
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            log.Error("원격 앱 재시작 실패: WPF 애플리케이션을 찾지 못했습니다.");
            Interlocked.Exchange(ref _restartQueued, 0);
            return;
        }
        _ = app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }
}
