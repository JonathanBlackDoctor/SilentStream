namespace SilentStream.Core.Contracts;

/// <summary>
/// A push-notification transport (원격 컨트롤러 개선 Phase 1). Implementations deliver one text
/// message to the operator's phone (Telegram now; PWA Web Push joins in Phase 3). The health
/// notification service fans <see cref="Models.HealthEvent"/>s out to every registered notifier;
/// the control window's "테스트 알림" button calls it directly.
/// </summary>
public interface INotifier
{
    /// <summary>
    /// Sends one text message. Returns false when the channel is unconfigured/disabled or delivery
    /// failed — failures are logged, never thrown, so a dead network can't hurt the health pipeline.
    /// </summary>
    Task<bool> SendAsync(string message, CancellationToken ct);
}
