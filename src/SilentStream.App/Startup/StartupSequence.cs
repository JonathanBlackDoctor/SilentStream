using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SilentStream.Core.Contracts;
using SilentStream.Core.SingleInstance;

namespace SilentStream.App;

/// <summary>
/// Boot-time automation (plan §4.2/§4.3): single-instance enforcement, auto-start
/// registration, the automatic stream+record start (the 30s warmup lives inside the
/// orchestrator), and the session-ending hook that finalises the mp4 and completes
/// the broadcast on PC shutdown/logoff.
/// </summary>
public static class StartupSequence
{
    private static SingleInstanceGuard? _guard;
    private static IDisposable? _signalListener;
    private static CancellationTokenSource? _lifetimeCts;

    public static async Task RunAsync(IServiceProvider services, Application app)
    {
        var log = services.GetRequiredService<ILogService>();
        var orchestrator = services.GetRequiredService<IStreamOrchestrator>();
        var configStore = services.GetRequiredService<IConfigStore>();

        // 단일 인스턴스 (plan §3.1): 중복 실행은 기존 인스턴스에 신호만 보내고 종료.
        _guard = new SingleInstanceGuard();
        if (!_guard.IsPrimaryInstance)
        {
            log.Info("이미 실행 중인 인스턴스가 있어 제어판 표시 신호만 보내고 종료합니다.");
            SingleInstanceGuard.SignalPrimaryInstance();
            app.Dispatcher.Invoke(app.Shutdown);
            return;
        }
        _signalListener = _guard.ListenForSignals(() =>
            (app as App)?.ShowControlWindow());

        // 자동 시작 등록을 현재 설정과 동기화.
        new AutoStartManager(log).Apply(configStore.Load().Autostart);

        // 종료 시퀀스 후킹 (plan §4.3): mp4 마감 → complete 전이 → 로그 flush.
        _lifetimeCts = new CancellationTokenSource();
        SystemEvents.SessionEnding += (_, _) => OnSessionEnding(orchestrator, log);
        app.Dispatcher.Invoke(() => app.SessionEnding += (_, _) => OnSessionEnding(orchestrator, log));
        app.Dispatcher.Invoke(() => app.Exit += (_, _) => Cleanup());

        // 자동 송출+녹화 시작 (30초 워밍업은 orchestrator 내부).
        await orchestrator.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
    }

    private static void OnSessionEnding(IStreamOrchestrator orchestrator, ILogService log)
    {
        log.Info("세션 종료 감지 — 녹화 파일을 마감하고 브로드캐스트를 종료합니다.");
        _lifetimeCts?.Cancel();
        try
        {
            // Windows가 허용하는 짧은 창 안에 동기적으로 마감해야 한다.
            orchestrator.StopAsync().Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            log.Error("종료 시퀀스 중 오류(frag mp4라 파일은 재생 가능)", ex);
        }
        Core.Logging.LogConfigurator.Flush();
    }

    private static void Cleanup()
    {
        _signalListener?.Dispose();
        _guard?.Dispose();
        _lifetimeCts?.Dispose();
    }
}
