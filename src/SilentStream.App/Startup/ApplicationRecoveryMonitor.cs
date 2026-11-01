using System.Diagnostics;
using System.IO;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.Safety;

namespace SilentStream.App;

public sealed class ApplicationRecoveryMonitor(ApplicationSafetySession safety, IStreamOrchestrator orchestrator,
    ILogService log) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Process? _companion;
    private bool _disabled;

    public void Start()
    {
        if (safety.RunId is null || safety.PreservationHeld) return;
        var helper = Path.Combine(AppContext.BaseDirectory, "uninstall-helper", "MediaCaptureHelper.UninstallHelper.exe");
        var executable = Environment.ProcessPath;
        if (!File.Exists(helper) || string.IsNullOrWhiteSpace(executable))
        { log.Warn("앱 종료 감시 도우미가 없어 자동 재기동을 사용할 수 없습니다."); return; }
        safety.PersistenceFailed += Disable;
        orchestrator.StateChanged += OnStateChanged;
        try
        {
            using var current = Process.GetCurrentProcess();
            var command = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--watch-app", current.Id.ToString(),
                         current.StartTime.ToUniversalTime().Ticks.ToString(), executable, safety.DirectoryPath, safety.RunId })
                command.ArgumentList.Add(argument);
            _companion = Process.Start(command);
            if (_companion is null) { Disable(); return; }
            _ = PulseAsync();
        }
        catch (Exception ex) { log.Error("앱 종료 감시 시작 실패", ex); Disable(); }
    }

    private void OnStateChanged(object? sender, StreamState state) => Pulse();
    private void Pulse()
    {
        if (_disabled) return;
        var expected = orchestrator.State is StreamState.Warmup or StreamState.ConnectingYouTube
            or StreamState.Live or StreamState.RecordingOnly or StreamState.Retrying;
        if (!safety.Heartbeat(expected, DateTimeOffset.UtcNow)) Disable();
    }
    private async Task PulseAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && !_disabled)
            {
                Pulse();
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Disable()
    {
        _disabled = true;
        // A stop that cannot be persisted must not leave a companion authorised by stale intent.
        // This is only the exact helper we launched, never the recorder or another application.
        try { if (_companion is { HasExited: false }) _companion.Kill(); } catch { }
        log.Error("실행 의도 저장/감시 실패로 자동 재기동을 차단했습니다. 현재 녹화와 원본을 확인하세요.");
    }
    public void Dispose()
    {
        safety.PersistenceFailed -= Disable;
        orchestrator.StateChanged -= OnStateChanged;
        _stop.Cancel();
        _companion?.Dispose();
        // Do not kill here: an unhandled process failure must remain observable externally.
    }
}
