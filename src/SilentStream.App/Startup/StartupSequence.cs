using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
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
    private static IRemoteControlServer? _remoteServer;

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

        // 확장(교시 VOD + 폰 원격): 라이브와 독립적으로 스케줄러/업로드 워커/원격 서버를 먼저 기동한다.
        // (orchestrator.StartAsync 는 YouTube 미연결 시 재시도 루프로 반환하지 않을 수 있으므로 앞에 둔다.)
        services.GetRequiredService<VodCoordinator>().Start(_lifetimeCts.Token);
        // 헬스 모니터 + 폰 푸시: 상태 감시를 시작하고, 헬스 이벤트를 텔레그램 등 알림 채널로 전달한다.
        // 알림 서비스를 먼저 구독시켜 첫 이벤트부터 놓치지 않는다. 손으로 붙여넣은 평문 봇 토큰은
        // 알림 사용 여부와 무관하게 지금 즉시 DPAPI 암호화한다(평문이 디스크에 남지 않도록).
        services.GetRequiredService<TelegramNotifier>().MigratePlaintextTokenAtRest();
        services.GetRequiredService<HealthNotificationService>().Start(_lifetimeCts.Token);
        services.GetRequiredService<IHealthMonitor>().Start(_lifetimeCts.Token);
        await StartRemoteServerAsync(services, log, _lifetimeCts.Token).ConfigureAwait(false);

        // 자동 송출+녹화 시작 (30초 워밍업은 orchestrator 내부). 원격에서 자동 운행을
        // 껐으면 제어 서버만 유지해, 현장 방문 없이 다시 켜거나 수동으로 시작할 수 있게 한다.
        if (configStore.Load().AutomaticOperationEnabled)
        {
            await orchestrator.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        else
        {
            log.Warn("자동 녹화·방송이 중지되어 있습니다. 원격 제어와 수동 시작만 사용할 수 있습니다.");
        }
    }

    /// <summary>
    /// Starts the embedded remote-control server per config.remote.mode (확장계획서 §4.4). Off by
    /// default; failures are logged but never block the live/recording path.
    /// </summary>
    private static async Task StartRemoteServerAsync(
        IServiceProvider services, ILogService log, CancellationToken ct)
    {
        var remote = services.GetService<IRemoteControlServer>();
        var config = services.GetRequiredService<IConfigStore>().Load().Remote;
        if (remote is null)
        {
            return;
        }

        var mode = config.Mode?.ToLowerInvariant() switch
        {
            "lan" => RemoteBindMode.Lan,
            "tailscale" => RemoteBindMode.Tailscale,
            "cloudflare" => RemoteBindMode.Cloudflare,
            _ => RemoteBindMode.Off
        };

        try
        {
            await remote.StartAsync(mode, config.Port, ct).ConfigureAwait(false);
            _remoteServer = remote;
        }
        catch (Exception ex)
        {
            log.Error("원격 제어 서버 시작 실패(라이브/녹화에는 영향 없음)", ex);
        }
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
        try
        {
            _remoteServer?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // best-effort
        }
        _signalListener?.Dispose();
        _guard?.Dispose();
        _lifetimeCts?.Dispose();
    }
}
