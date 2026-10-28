namespace SilentStream.Core.YouTube;

/// <summary>
/// Raised by <c>IYouTubeUploadService</c> when YouTube rejects an upload because the daily
/// API quota is spent (HTTP 403 quotaExceeded / dailyLimitExceeded or HTTP 429 Video Uploads/day). The upload queue keeps
/// the job pending and pauses its worker until the Pacific-time quota reset (확장계획서 §4.2, D7).
/// </summary>
public sealed class QuotaExceededException : Exception
{
    /// <summary>Channel-level upload limits have a separate cooldown, not the API PT reset.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public QuotaExceededException(string message) : base(message) { }

    public QuotaExceededException(string message, Exception inner) : base(message, inner) { }
}
