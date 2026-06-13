using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SilentStream.App.ControlUI;
using SilentStream.App.Hotkeys;
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
/// 9px status box and the hotkey-toggled control window. The automated start
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
        collection.AddSingleton<MainViewModel>();
        _services = collection.BuildServiceProvider();

        var log = _services.GetRequiredService<ILogService>();
        var configStore = _services.GetRequiredService<IConfigStore>();
        var config = configStore.Load();

        if (!EnsureConsent(configStore))
        {
            Shutdown();
            return;
        }

        // 9px status box (always visible, click-through).
        _statusBox = new StatusBoxWindow();
        _statusBox.Show();
        var orchestrator = _services.GetRequiredService<IStreamOrchestrator>();
        orchestrator.StateChanged += (_, state) => _statusBox.SetState(state);
        _statusBox.SetState(orchestrator.State);

        // Hotkey-toggled control window.
        var viewModel = _services.GetRequiredService<MainViewModel>();
        _controlWindow = new ControlWindow(viewModel);
        _hotkeyManager = new HotkeyManager(log);
        _hotkeyManager.HotkeyPressed += () => _controlWindow.Toggle();
        _hotkeyManager.Register(config.Hotkey);
        viewModel.HotkeyChanged += gesture => _hotkeyManager.Register(gesture);

        _updateManager = new AppUpdateManager(log);
        _updateManager.Start();

        log.Info("SilentStream 시작 완료 (백그라운드 대기)");

        _ = StartupSequence.RunAsync(_services, this);
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
