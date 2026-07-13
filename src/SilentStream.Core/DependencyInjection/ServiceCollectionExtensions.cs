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
        // One shared, token-safe OAuth health source for the live and VOD paths.
        services.AddSingleton<IYouTubeAuthHealth, YouTubeAuthHealth>();
        services.AddSingleton<IYouTubeLiveService, YouTubeLiveService>();
        services.AddSingleton<IRecordingManager, RecordingManager>();
        // 적응형 송출 품질(확장계획서_적응형송출품질): 정책 컨트롤러. 오케스트레이터가 메트릭/상태를
        // 밀어넣고 ChangeRequested를 받아 스왑을 실행한다; 원격 서버는 수동 고정(SetManual)에 쓴다.
        services.AddSingleton<IAdaptiveQualityController, AdaptiveQualityController>();
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
        services.AddSingleton<ILocalAudioExportService, LocalAudioExportService>();
        services.AddSingleton<IPeriodAssetCatalog, PeriodAssetCatalog>();
        services.AddSingleton<IYouTubeUploadService, YouTubeUploadService>();
        services.AddSingleton<IYouTubeCaptionService, YouTubeCaptionService>();
        services.AddSingleton<IUploadQueue, UploadQueue>();
        // 승인 기반 교시 분할(v8): 경계마다 즉시 컷 대신 승인 대기 생성 → 폰에서 승인/조정/연강/
        // 건너뛰기, 미조작 시 자동 승인. 원격 서버가 Changed 이벤트를 구독해 폰으로 푸시한다.
        services.AddSingleton<ISplitApprovalService, SplitApprovalService>();
        services.AddSingleton<VodCoordinator>();

        // 헬스/이벤트 레이어(원격 컨트롤러 개선 Phase 0): 오케스트레이터·믹서·녹화·업로드 큐를 구독·폴링해
        // 타입드 HealthEvent(무음/송출끊김/디스크부족/업로드실패/라이브 시작·종료)로 방출한다. 폰 푸시·
        // 멀티 호실·UI가 공통으로 이 레이어를 소비한다.
        services.AddSingleton<IHealthMonitor, HealthMonitor>();

        // 폰 푸시 알림(Phase 1): HealthEvent → 심각도 필터 → 채널 팬아웃. 채널은 INotifier로 추가
        // 등록한다(현재 텔레그램; Phase 3에서 PWA Web Push 합류 예정). TelegramNotifier는 기동 시
        // 평문 토큰 즉시 암호화(MigratePlaintextTokenAtRest) 호출을 위해 구체 타입으로도 노출한다.
        services.AddSingleton<TelegramNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<TelegramNotifier>());
        // 폰 PWA Web Push(Phase 3): 텔레그램과 나란히 붙는 2번째 INotifier — HealthNotificationService가
        // IEnumerable<INotifier>로 둘 다 팬아웃한다. 구독 목록·VAPID 키는 config가 아닌 전용 파일에 두어
        // 스키마를 건드리지 않는다. 서비스워커/푸시는 보안 컨텍스트가 필요하므로 실제 수신은 HTTPS(Cloudflare
        // 호스트네임)에서만 되고, LAN 평문 경로는 텔레그램이 계속 담당한다.
        services.AddSingleton<IPushSubscriptionStore>(_ => new PushSubscriptionStore(AppPaths.PushSubscriptionsFile));
        services.AddSingleton<IVapidKeyStore>(sp =>
            new VapidKeyStore(AppPaths.VapidKeysFile, sp.GetRequiredService<ITokenProtector>()));
        services.AddSingleton<WebPushNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<WebPushNotifier>());
        services.AddSingleton<HealthNotificationService>();
        return services;
    }
}
