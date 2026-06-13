using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// YouTube Data API v3 live integration (plan §3.7): installed-app OAuth2 with the token
/// stored DPAPI-encrypted in config.json, a fresh unlisted broadcast per session
/// (liveBroadcasts.insert + liveStreams.insert + bind), and a best-effort complete
/// transition at shutdown.
/// </summary>
public sealed class YouTubeLiveService(
    IConfigStore configStore,
    ITokenProtector tokenProtector,
    ILogService log) : IYouTubeLiveService
{
    private YouTubeService? _service;

    public async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        if (!File.Exists(AppPaths.ClientSecretFile))
        {
            log.Error($"OAuth 클라이언트 설정 파일이 없습니다: {AppPaths.ClientSecretFile} " +
                      "(준비 방법은 CLAUDE.local.md / docs/CLAUDE.local.md.template 참고)");
            return false;
        }

        try
        {
            await using var secretStream = File.OpenRead(AppPaths.ClientSecretFile);
            var secrets = await GoogleClientSecrets.FromStreamAsync(secretStream, ct).ConfigureAwait(false);

            // First run opens the browser; afterwards the encrypted refresh token renews silently.
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
                ApplicationName = "SilentStream"
            });
            log.Info("YouTube 인증 완료");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("YouTube 인증 실패", ex);
            return false;
        }
    }

    public async Task<LiveSession> CreateBroadcastAsync(CancellationToken ct)
    {
        var service = _service
            ?? throw new InvalidOperationException("AuthenticateAsync를 먼저 호출해야 합니다.");
        var youtubeConfig = configStore.Load().YouTube;
        var title = TitleTemplater.Expand(youtubeConfig.TitleTemplate, DateTime.Now);

        // 1) Broadcast (the watch-page entity). Auto start/stop follows the encoder feed.
        var broadcast = new LiveBroadcast
        {
            Snippet = new LiveBroadcastSnippet
            {
                Title = title,
                ScheduledStartTimeDateTimeOffset = DateTimeOffset.UtcNow
            },
            Status = new LiveBroadcastStatus
            {
                PrivacyStatus = youtubeConfig.Privacy, // plan fixes this to "unlisted"
                SelfDeclaredMadeForKids = false
            },
            ContentDetails = new LiveBroadcastContentDetails
            {
                EnableAutoStart = true,
                EnableAutoStop = true
            }
        };
        broadcast = await service.LiveBroadcasts
            .Insert(broadcast, "snippet,status,contentDetails")
            .ExecuteAsync(ct).ConfigureAwait(false);

        // 2) Stream (the RTMP ingest endpoint).
        var stream = new LiveStream
        {
            Snippet = new LiveStreamSnippet { Title = title },
            Cdn = new CdnSettings
            {
                IngestionType = "rtmp",
                Resolution = "variable",
                FrameRate = "variable"
            }
        };
        stream = await service.LiveStreams
            .Insert(stream, "snippet,cdn")
            .ExecuteAsync(ct).ConfigureAwait(false);

        // 3) Bind them together.
        var bind = service.LiveBroadcasts.Bind(broadcast.Id, "id,contentDetails");
        bind.StreamId = stream.Id;
        await bind.ExecuteAsync(ct).ConfigureAwait(false);

        var ingest = stream.Cdn.IngestionInfo;
        log.Info($"브로드캐스트 생성: \"{title}\" (id={broadcast.Id}, 공개범위={youtubeConfig.Privacy})");

        return new LiveSession(
            broadcast.Id,
            ingest.StreamName,
            ingest.IngestionAddress,
            $"https://www.youtube.com/watch?v={broadcast.Id}");
    }

    public async Task CompleteBroadcastAsync(string broadcastId, CancellationToken ct)
    {
        var service = _service;
        if (service is null || string.IsNullOrEmpty(broadcastId))
        {
            return;
        }

        try
        {
            await service.LiveBroadcasts
                .Transition(LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Complete,
                    broadcastId, "status")
                .ExecuteAsync(ct).ConfigureAwait(false);
            log.Info($"브로드캐스트 종료(complete) 전이 완료: {broadcastId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Plan §3.7: failure here is tolerated — the next boot creates a new broadcast.
            log.Warn($"브로드캐스트 complete 전이 실패(다음 부팅 시 새로 생성): {ex.Message}");
        }
    }
}
