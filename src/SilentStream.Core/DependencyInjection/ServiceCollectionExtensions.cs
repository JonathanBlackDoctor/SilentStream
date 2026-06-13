using Microsoft.Extensions.DependencyInjection;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;

namespace SilentStream.Core.DependencyInjection;

/// <summary>
/// Composition root for the SilentStream core services. Every module is bound to its
/// <c>Core/Contracts</c> interface only — see plan §2.2 (모듈 간 계약). Phase 0 registers
/// the stub implementations; later phases swap in the real ones behind the same interfaces.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core services with their fixed contracts. Constructor injection;
    /// singletons because each represents a single long-lived runtime component.
    /// </summary>
    public static IServiceCollection AddSilentStreamCore(this IServiceCollection services)
    {
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
        services.AddSingleton<IConfigStore, ConfigStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IScreenCaptureSource, ScreenCaptureSource>();
        services.AddSingleton<IAudioMixer, AudioMixer>();
        services.AddSingleton<IEncoderPipeline, EncoderPipeline>();
        services.AddSingleton<IYouTubeLiveService, YouTubeLiveService>();
        services.AddSingleton<IRecordingManager, RecordingManager>();
        services.AddSingleton<IStreamOrchestrator, StreamOrchestrator>();
        return services;
    }
}
