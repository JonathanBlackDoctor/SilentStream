using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SilentStream.App.ControlUI;
using SilentStream.App.Hotkeys;
using SilentStream.App.Preview;
using SilentStream.App.Remote;
using SilentStream.App.StatusIndicator;
using SilentStream.App.Updates;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.Core.DependencyInjection;
using SilentStream.Core.Logging;
using SilentStream.Media.Windows;

namespace SilentStream.App;

/// <summary>
/// Background WPF host: builds the DI container (Core contracts + Windows media
/// implementations), shows the one-time consent dialog, then runs headless with the
/// 6px status box and the hotkey-toggled control window. The automated start
/// sequence (mutex/warmup/auto-stream) is wired in <see cref="StartupSequence"/>.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private StatusBoxWindow? _statusBox;
    private ControlWindow? _controlWindow;
    private HotkeyManager? _hotkeyManager;
    private AppUpdateManager? _updateManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LogConfigurator.Configure();

        var collection = new ServiceCollection();
        collection.AddSilentStreamCore();
        // Swap the Phase-0 stubs for the real Windows capture/audio implementations.
        collection.AddSingleton<IScreenCaptureSource, DxgiScreenCaptureSource>();
        collection.AddSingleton<IAudioMixer, WasapiAudioMixer>();
        // 송출 미리보기: 캡처 프레임을 저레이트 JPEG 썸네일로 제공(제어창 + 폰).
        collection.AddSingleton<IPreviewProvider, FramePreviewService>();
        // 확장(폰 원격 제어): 임베디드 Kestrel 서버. 구체 타입은 제어창 PIN 표시에 사용.
        collection.AddSingleton<RemoteControlServer>();
        collection.AddSingleton<IRemoteControlServer>(sp => sp.GetRequiredService<RemoteControlServer>());
        collection.AddSingleton<MainViewModel>();
        _services = collection.BuildServiceProvider();

        var log = _services.GetRequiredService<ILogService>();
        // Velopack 패키지에 동봉한 OAuth client_secret 을 사용자 폴더에 1회 배치(없을 때만).
        // 기존 Inno [Files] onlyifdoesntexist 단계를 대체 — 토큰/시크릿 등 사용자 자산은 보존한다.
        SeedBundledClientSecret(log);
        var configStore = _services.GetRequiredService<IConfigStore>();
        var config = configStore.Load();

        if (!EnsureConsent(configStore))
        {
            Shutdown();
            return;
        }

        // 6px status box (always visible, click-through).
        _statusBox = new StatusBoxWindow();
        _statusBox.Show();
        var orchestrator = _services.GetRequiredService<IStreamOrchestrator>();
        orchestrator.StateChanged += (_, state) => _statusBox.SetState(state);
        _statusBox.SetState(orchestrator.State);

        // Hotkey-toggled control window.
        var viewModel = _services.GetRequiredService<MainViewModel>();
        // 제어창 생성 전에 현재 버전을 넣어 제목이 첫 바인딩부터 올바르게 표시되도록 한다.
        var version = GetAppVersion();
        viewModel.SetVersion(version);
        _controlWindow = new ControlWindow(viewModel);
        _hotkeyManager = new HotkeyManager(log);
        _hotkeyManager.HotkeyPressed += () => _controlWindow.Toggle();
        _hotkeyManager.Register(config.Hotkey);
        viewModel.HotkeyChanged += gesture => _hotkeyManager.Register(gesture);

        // 폰 페어링 PIN을 제어창에 표시 (원격 서버는 StartupSequence에서 기동).
        var remoteServer = _services.GetRequiredService<RemoteControlServer>();
        remoteServer.PinChanged += pin => viewModel.SetRemotePin(pin);
        if (remoteServer.CurrentPin is not null)
        {
            viewModel.SetRemotePin(remoteServer.CurrentPin);
        }
        // 외부 접속용 Cloudflare 터널 공개 주소(준비되면 도착, PIN보다 몇 초 늦을 수 있음).
        remoteServer.PublicUrlChanged += url => viewModel.SetRemoteUrl(url);
        if (remoteServer.CurrentPublicUrl is not null)
        {
            viewModel.SetRemoteUrl(remoteServer.CurrentPublicUrl);
        }

        _updateManager = new AppUpdateManager(log);
        // 새 버전을 내려받아 적용 예약하면 제어창 상태바에 "재시작 시 적용"을 띄운다.
        _updateManager.UpdateStaged += staged => viewModel.SetStagedUpdate(staged);
        _updateManager.Start();

        log.Info($"SilentStream v{version} 시작 완료 (백그라운드 대기)");

        _ = StartupSequence.RunAsync(_services, this);
    }

    /// <summary>
    /// 현재 실행 중인 버전 문자열을 Velopack 릴리스(피드) 버전과 글자 그대로 일치하게 돌려준다(예 "0.2.0").
    /// InformationalVersion을 쓰는 이유: AssemblyVersion(4부 숫자)은 "0.2.0-beta.1" 같은 SemVer 사전배포
    /// 라벨을 못 담아 Velopack이 주는 스테이징 업데이트 문자열과 어긋난다. SDK가 git 빌드에서 덧붙이는
    /// "+&lt;커밋해시&gt;" 빌드 메타데이터는 잘라낸다. CI는 릴리스 태그에서 <c>-p:Version=</c>으로 이 값을 채우고,
    /// 개발 빌드는 csproj 기본값(현재 0.1.0)을 쓴다. Velopack이 업데이트를 적용하면 교체된 exe 값이 그대로 반영된다.
    /// </summary>
    private static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+'); // "0.2.0+abc123" → "0.2.0" (빌드 메타데이터 제거)
            return plus >= 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version; // InformationalVersion 특성이 없는 빌드 대비 폴백
        return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Copies the OAuth client secret bundled in the install dir to %AppData%\SilentStream
    /// on first run (only when absent, so the user's token/secret survive updates). Velopack
    /// ships client_secret.json next to the exe; a dev build without it is a silent no-op.
    /// </summary>
    private static void SeedBundledClientSecret(ILogService log)
    {
        try
        {
            var target = AppPaths.ClientSecretFile;
            if (File.Exists(target))
            {
                return; // 사용자 자산 보존 (Inno onlyifdoesntexist 동등)
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "client_secret.json");
            if (!File.Exists(bundled))
            {
                return; // 번들에 시크릿이 없는 빌드(로컬 dev 등) — 사용자가 수동 배치
            }

            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.Copy(bundled, target);
            log.Info("번들된 OAuth client_secret.json 을 사용자 폴더에 배치했습니다.");
        }
        catch (Exception ex)
        {
            log.Warn($"OAuth client_secret 배치 실패(최초 로그인 시 수동 배치가 필요할 수 있음): {ex.Message}");
        }
    }

    /// <summary>Shows the consent dialog on first run; returns false when declined.</summary>
    private static bool EnsureConsent(IConfigStore configStore)
    {
        var marker = Path.Combine(AppPaths.AppDataDir, ".consent");
        if (File.Exists(marker))
        {
            return true;
        }

        var dialog = new ConsentWindow();
        dialog.ShowDialog();
        if (!dialog.Accepted)
        {
            return false;
        }

        Directory.CreateDirectory(AppPaths.AppDataDir);
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        return true;
    }

    /// <summary>Secondary instances call this on the primary via the named event.</summary>
    public void ShowControlWindow() =>
        Dispatcher.BeginInvoke(() => _controlWindow?.Toggle());

    protected override void OnExit(ExitEventArgs e)
    {
        _updateManager?.Dispose();
        _hotkeyManager?.Dispose();
        LogConfigurator.Flush();
        _services?.Dispose();
        base.OnExit(e);
    }
}
