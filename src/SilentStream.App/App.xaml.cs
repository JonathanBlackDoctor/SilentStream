using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SilentStream.Core.Contracts;
using SilentStream.Core.DependencyInjection;

namespace SilentStream.App;

/// <summary>
/// Background WPF host. Builds the DI container (core services bound to their contracts)
/// and resolves the orchestrator. The full start sequence (single-instance mutex, 30s
/// warmup, status box, hotkeys) is wired in later phases.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddSilentStreamCore();
        _services = collection.BuildServiceProvider();

        // Resolve the central coordinator so the container graph is validated at startup.
        // Phase 0 stops here; StartAsync is hooked up in Phase 5.
        _ = _services.GetRequiredService<IStreamOrchestrator>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
