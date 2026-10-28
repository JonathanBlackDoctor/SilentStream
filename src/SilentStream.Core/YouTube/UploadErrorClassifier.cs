using System.Net;
using Google;

namespace SilentStream.Core.YouTube;

/// <summary>Recognizes daily quota exhaustion without treating every HTTP 429 as a daily limit.</summary>
public static class UploadErrorClassifier
{
    public static bool IsChannelUploadLimit(Exception? exception) =>
        exception is GoogleApiException api &&
        api.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden &&
        (api.Error?.Errors?.Any(e => e.Reason == "uploadLimitExceeded") == true ||
         (api.Error?.Message ?? api.Message).Contains(
             "The user has exceeded the number of videos they may upload", StringComparison.OrdinalIgnoreCase));

    public static bool IsDailyQuota(Exception? exception)
    {
        if (exception is not GoogleApiException api) return false;
        if (api.HttpStatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            return false;
        if (api.Error?.Errors?.Any(e => e.Reason is "quotaExceeded" or "dailyLimitExceeded") == true)
            return true;
        // Google's Video Uploads/day limit also arrives as 429/RESOURCE_EXHAUSTED, without
        // the older YouTube reason list. Only the explicit daily metric belongs in this path.
        var message = api.Error?.Message ?? api.Message;
        return api.HttpStatusCode == HttpStatusCode.TooManyRequests &&
            message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Video Uploads per day", StringComparison.OrdinalIgnoreCase);
    }
}
