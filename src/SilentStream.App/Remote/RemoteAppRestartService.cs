using System.Diagnostics;
using System.IO;
using System.Windows;
using SilentStream.Core.Contracts;
using SilentStream.Core.Safety;

namespace SilentStream.App.Remote;

/// <summary>
/// Performs a remote-requested application restart without leaving ffmpeg or the embedded server
/// behind. The replacement process is launched by the bundled helper after this
/// process exits, so the single-instance guard never races the old instance.
/// </summary>
public sealed class RemoteAppRestartService(IStreamOrchestrator orchestrator, ILogService log, ApplicationSafetySession safety)
{
    private int _restartQueued;

    /// <summary>Queues one graceful restart and returns false when one is already pending.</summary>
    public bool RequestRestart()
    {
        if (Interlocked.CompareExchange(ref _restartQueued, 1, 0) != 0)
        {
            return false;
        }

        var handoff = safety.BeginTransition(ApplicationTransition.Restart, "remote-app-restart");
        if (handoff is null)
        {
            Interlocked.Exchange(ref _restartQueued, 0);
            return false;
        }
        _ = RestartAfterResponseAsync(handoff);
        return true;
    }

    private async Task RestartAfterResponseAsync(ApplicationRunState handoff)
    {
        // Let Kestrel flush the 202 response before its own shutdown closes the connection.
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

        var executable = Environment.ProcessPath;
        var helper = Path.Combine(AppContext.BaseDirectory, "uninstall-helper", "MediaCaptureHelper.UninstallHelper.exe");
        var app = Application.Current;
        if (safety.PreservationHeld || string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !File.Exists(helper) || app?.Dispatcher is null)
        {
            log.Error("원격 앱 재시작 실패: 앱 또는 재시작 도우미를 확인하지 못해 녹화를 유지합니다.");
            Interlocked.Exchange(ref _restartQueued, 0);
            safety.CancelTransition(handoff.TransitionToken!);
            return;
        }

        try
        {
            await orchestrator.StopForMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("녹화 마감을 확인하지 못해 앱 재시작을 중단했습니다.", ex);
            safety.CancelTransition(handoff.TransitionToken!);
            Interlocked.Exchange(ref _restartQueued, 0);
            return;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            var command = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            command.ArgumentList.Add("--restart-after");
            command.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ArgumentList.Add(executable);
            command.ArgumentList.Add(Path.Combine(SilentStream.Core.AppPaths.AppDataDir, "app-restart-result.json"));
            command.ArgumentList.Add(safety.DirectoryPath);
            command.ArgumentList.Add(handoff.RunId);
            command.ArgumentList.Add(handoff.TransitionToken!);
            command.ArgumentList.Add(handoff.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var helperProcess = Process.Start(command);
            if (helperProcess is null)
            {
                throw new InvalidOperationException("재시작 도우미 프로세스를 시작하지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            log.Error("원격 앱 재시작 실패: 새 인스턴스를 예약하지 못했습니다.", ex);
            Interlocked.Exchange(ref _restartQueued, 0);
            safety.CancelTransition(handoff.TransitionToken!);
            if (safety.StartAllowed && orchestrator.State == SilentStream.Core.Models.StreamState.Idle)
            {
                try
                {
                    if (safety.DesiredMode == DesiredRunMode.Live)
                        await orchestrator.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    else if (safety.DesiredMode == DesiredRunMode.RecordingOnly)
                        await orchestrator.StartRecordingSessionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError) { log.Error("재시작 실패 후 이전 녹화 상태 복구 실패", restoreError); }
            }
            return;
        }

        log.Info("원격 요청으로 앱을 다시 시작합니다.");
        _ = app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }
}
