using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Authorized YouTube caption downloader. The official <c>captions.list</c> and
/// <c>captions.download</c> endpoints require the <c>youtube.force-ssl</c> scope and edit
/// permission for the target video. API failures deliberately propagate so a transport layer
/// can map them to its own status response.
/// </summary>
public sealed class YouTubeCaptionService(
    ITokenProtector tokenProtector,
    ILogService log) : IYouTubeCaptionService
{
    private const string KoreanLanguage = "ko";

    private YouTubeService? _service;

    public async Task<CaptionDownload?> DownloadPreferredSrtAsync(
        string videoId,
        string? preferredLanguage,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        try
        {
            var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
            var response = await service.Captions
                .List("id,snippet", videoId)
                .ExecuteAsync(ct).ConfigureAwait(false);

            var caption = SelectCaption(response.Items, preferredLanguage);
            if (caption is null)
            {
                log.Info($"YouTube 다운로드 가능한 자막 없음: videoId={videoId}");
                return null;
            }

            await using var content = new MemoryStream();
            var request = service.Captions.Download(caption.Id);
            request.Tfmt = "srt";
            var progress = await request.DownloadAsync(content, ct).ConfigureAwait(false);
            if (progress.Status != DownloadStatus.Completed)
            {
                throw progress.Exception ?? new InvalidOperationException(
                    $"YouTube 자막 다운로드가 완료되지 않았습니다: videoId={videoId}, captionId={caption.Id}");
            }

            var snippet = caption.Snippet!;
            log.Info($"YouTube 자막 다운로드 완료: videoId={videoId}, captionId={caption.Id}, language={snippet.Language}");
            return new CaptionDownload(
                videoId,
                caption.Id,
                snippet.Language ?? string.Empty,
                snippet.Name,
                string.Equals(snippet.TrackKind, "ASR", StringComparison.OrdinalIgnoreCase),
                content.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"YouTube 자막 다운로드 실패: videoId={videoId}", ex);
            throw;
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

        await using var secretStream = File.OpenRead(AppPaths.ClientSecretFile);
        var secrets = await GoogleClientSecrets.FromStreamAsync(secretStream, ct).ConfigureAwait(false);
        // Keep this consent in a separate encrypted file. Existing live-stream installations can
        // grant force-ssl on demand without losing the token that keeps their broadcast running.
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            [YouTubeService.Scope.Youtube, YouTubeService.Scope.YoutubeForceSsl],
            "user",
            ct,
            new EncryptedFileTokenDataStore(AppPaths.YouTubeCaptionTokenFile, tokenProtector)).ConfigureAwait(false);

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

    private static Caption? SelectCaption(IEnumerable<Caption>? captions, string? preferredLanguage)
    {
        var tracks = captions?
            .Where(caption =>
                !string.IsNullOrWhiteSpace(caption.Id) &&
                IsServing(caption.Snippet))
            .ToList();
        if (tracks is not { Count: > 0 })
        {
            return null;
        }

        var preferred = FindLanguageMatch(tracks, preferredLanguage);
        if (preferred is not null)
        {
            return preferred;
        }

        return FindLanguageMatch(tracks, KoreanLanguage) ?? tracks[0];
    }

    private static Caption? FindLanguageMatch(IReadOnlyList<Caption> tracks, string? requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return null;
        }

        var requested = requestedLanguage.Trim();
        var exact = tracks.FirstOrDefault(caption =>
            string.Equals(caption.Snippet!.Language, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var primaryLanguage = PrimaryLanguage(requested);
        return tracks.FirstOrDefault(caption =>
            string.Equals(PrimaryLanguage(caption.Snippet!.Language), primaryLanguage,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string PrimaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var separator = language.IndexOfAny(['-', '_']);
        return separator < 0 ? language : language[..separator];
    }

    private static bool IsServing(CaptionSnippet? snippet) =>
        string.Equals(snippet?.Status, "serving", StringComparison.OrdinalIgnoreCase);
}
