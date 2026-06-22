using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Per-period VOD uploader (확장계획서 §4.2): YouTube Data API v3 <c>videos.insert</c> with a
/// resumable upload, reusing the DPAPI-stored OAuth token (full scope already covers uploads,
/// D9 — no second login). Throttles the read stream to protect the live uplink when
/// uploadTiming is "immediate-throttled" (D10). 403 quota errors surface as
/// <see cref="QuotaExceededException"/> so the queue can defer to the next day (D7).
/// </summary>
public sealed class YouTubeUploadService(
    IConfigStore configStore,
    ITokenProtector tokenProtector,
    ILogService log) : IYouTubeUploadService
{
    /// <summary>~12 Mbps cap for immediate-throttled uploads — leaves headroom for the live RTMP.</summary>
    private const long ThrottleBytesPerSecond = 1_500_000;

    private YouTubeService? _service;

    public async Task<string> UploadAsync(string filePath, string title, string privacy, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("업로드할 교시 파일이 없습니다.", filePath);
        }

        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);

        var video = new Video
        {
            Snippet = new VideoSnippet { Title = title },
            Status = new VideoStatus
            {
                PrivacyStatus = privacy,            // "unlisted" (D4)
                SelfDeclaredMadeForKids = false
            }
        };

        await using var fileStream = File.OpenRead(filePath);
        var throttle = configStore.Load().Periods.UploadTiming == "immediate-throttled";
        Stream uploadStream = throttle
            ? new ThrottledStream(fileStream, ThrottleBytesPerSecond)
            : fileStream;

        try
        {
            var request = service.Videos.Insert(video, "snippet,status", uploadStream, "video/mp4");
            log.Info($"VOD 업로드 시작: \"{title}\" (공개범위={privacy}, throttle={throttle}).");

            var progress = await request.UploadAsync(ct).ConfigureAwait(false);
            if (progress.Status != UploadStatus.Completed)
            {
                throw Classify(progress.Exception, title);
            }

            var videoId = request.ResponseBody?.Id
                          ?? throw new InvalidOperationException("업로드는 완료됐으나 videoId를 받지 못했습니다.");
            log.Info($"VOD 업로드 완료: \"{title}\" → https://youtu.be/{videoId}");
            return videoId;
        }
        finally
        {
            if (throttle)
            {
                await uploadStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<YouTubeService> EnsureServiceAsync(CancellationToken ct)
    {
        if (_service is not null)
        {
            return _service;
        }
        if (!File.Exists(AppPaths.ClientSecretFile))
        {
            throw new InvalidOperationException(
                $"OAuth 클라이언트 설정 파일이 없습니다: {AppPaths.ClientSecretFile}");
        }

        // Same data store + "user" key as the live service: reuses the already-stored refresh
        // token (full scope includes videos.insert, D9) so no interactive prompt is triggered.
        await using var secretStream = File.OpenRead(AppPaths.ClientSecretFile);
        var secrets = await GoogleClientSecrets.FromStreamAsync(secretStream, ct).ConfigureAwait(false);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            [YouTubeService.Scope.Youtube],
            "user",
            ct,
            new EncryptedTokenDataStore(configStore, tokenProtector)).ConfigureAwait(false);

        if (credential.Token.IsStale)
        {
            await credential.RefreshTokenAsync(ct).ConfigureAwait(false);
        }

        _service = new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Media Capture Helper"
        });
        return _service;
    }

    /// <summary>Maps a 403 quota error to <see cref="QuotaExceededException"/>; rethrows the rest.</summary>
    private static Exception Classify(Exception? uploadException, string title)
    {
        if (uploadException is GoogleApiException api && IsQuota(api))
        {
            return new QuotaExceededException($"YouTube 일일 업로드 quota 초과: \"{title}\"", api);
        }
        return uploadException ?? new InvalidOperationException($"VOD 업로드 실패: \"{title}\"");
    }

    private static bool IsQuota(GoogleApiException api) =>
        api.HttpStatusCode == System.Net.HttpStatusCode.Forbidden &&
        api.Error?.Errors?.Any(e =>
            e.Reason is "quotaExceeded" or "dailyLimitExceeded" or "rateLimitExceeded") == true;
}
