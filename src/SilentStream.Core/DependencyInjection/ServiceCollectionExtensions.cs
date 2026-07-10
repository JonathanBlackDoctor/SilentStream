using Microsoft.Extensions.DependencyInjection;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;

namespace SilentStream.Core.DependencyInjection;

/// <summary>
/// Composition root for the Media Capture Helper core services. Every module is bound to its
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
        // The same RecordingManager instance exposes the read-only session view for the VOD cut.
        services.AddSingleton<IRecordingSessionInfo>(sp =>
            (IRecordingSessionInfo)sp.GetRequiredService<IRecordingManager>());
        services.AddSingleton<IStreamOrchestrator, StreamOrchestrator>();

        // 확장(교시 VOD): 시간표 스토어 + 벽시계 스케줄러. PeriodScheduler는 NotifyScheduleChanged
        // 호출을 위해 구체 타입으로도 해석 가능하게 한 인스턴스를 공유한다.
        services.AddSingleton<IPeriodScheduleStore, PeriodScheduleStore>();
        services.AddSingleton<PeriodScheduler>();
        services.AddSingleton<IPeriodScheduler>(sp => sp.GetRequiredService<PeriodScheduler>());
        services.AddSingleton<IVodSegmentService, VodSegmentService>();
        services.AddSingleton<IYouTubeUploadService, YouTubeUploadService>();
        services.AddSingleton<IUploadQueue, UploadQueue>();
        services.AddSingleton<VodCoordinator>();

        // 헬스/이벤트 레이어(원격 컨트롤러 개선 Phase 0): 오케스트레이터·믹서·녹화·업로드 큐를 구독·폴링해
        // 타입드 HealthEvent(무음/송출끊김/디스크부족/업로드실패/라이브 시작·종료)로 방출한다. 폰 푸시·
        // 멀티 호실·UI가 공통으로 이 레이어를 소비한다.
        services.AddSingleton<IHealthMonitor, HealthMonitor>();
        return services;
    }
}
