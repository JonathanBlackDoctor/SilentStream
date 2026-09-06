using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Small YouTube Data API adapter used by the durable daily-playlist service. Background work
/// never launches an interactive OAuth browser: it only reuses the encrypted existing token.
/// </summary>
public sealed class YouTubePlaylistGateway(
    IConfigStore configStore,
    ITokenProtector tokenProtector,
    IYouTubeAuthHealth authHealth) : IYouTubePlaylistGateway
{
    private readonly SemaphoreSlim _serviceGate = new(1, 1);
    private YouTubeService? _service;
    private GoogleAuthorizationCodeFlow? _flow;

    public async Task<string?> FindPlaylistByMarkerAsync(string marker, CancellationToken ct)
    {
        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
        string? pageToken = null;
        do
        {
            var request = service.Playlists.List("id,snippet");
            request.Mine = true;
            request.MaxResults = 50;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
            var match = response.Items?.FirstOrDefault(item =>
                item.Snippet?.Description?.Contains(marker, StringComparison.Ordinal) == true);
            if (!string.IsNullOrWhiteSpace(match?.Id))
            {
                return match.Id;
            }
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return null;
    }

    public async Task<string> CreatePlaylistAsync(string title, string description, CancellationToken ct)
    {
        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
        var playlist = new Playlist
        {
            Snippet = new PlaylistSnippet { Title = title, Description = description },
            Status = new PlaylistStatus { PrivacyStatus = "unlisted" }
        };
        var created = await service.Playlists.Insert(playlist, "snippet,status")
            .ExecuteAsync(ct).ConfigureAwait(false);
        return created.Id ?? throw new InvalidOperationException("YouTube가 재생목록 ID를 반환하지 않았습니다.");
    }

    public async Task<bool> ContainsVideoAsync(string playlistId, string videoId, CancellationToken ct)
    {
        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
        var request = service.PlaylistItems.List("id");
        request.PlaylistId = playlistId;
        request.VideoId = videoId;
        request.MaxResults = 1;
        var response = await request.ExecuteAsync(ct).ConfigureAwait(false);
        return response.Items is { Count: > 0 };
    }

    public async Task AddVideoAsync(string playlistId, string videoId, CancellationToken ct)
    {
        var service = await EnsureServiceAsync(ct).ConfigureAwait(false);
        var item = new PlaylistItem
        {
            Snippet = new PlaylistItemSnippet
            {
                PlaylistId = playlistId,
                ResourceId = new ResourceId { Kind = "youtube#video", VideoId = videoId }
            }
        };
        await service.PlaylistItems.Insert(item, "snippet").ExecuteAsync(ct).ConfigureAwait(false);
    }

    private async Task<YouTubeService> EnsureServiceAsync(CancellationToken ct)
    {
        if (_service is not null)
        {
            return _service;
        }

        await _serviceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_service is not null)
            {
                return _service;
            }
            if (!File.Exists(AppPaths.ClientSecretFile))
            {
                authHealth.ReportPermanentFailure(YouTubeAuthFailureKind.MissingClientSecret);
                throw new YouTubeAuthorizationRequiredException("YouTube OAuth 클라이언트 설정이 필요합니다.");
            }

            var store = new EncryptedTokenDataStore(configStore, tokenProtector);
            var token = await store.GetAsync<TokenResponse>("user").ConfigureAwait(false);
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new YouTubeAuthorizationRequiredException("앱에서 YouTube 계정을 먼저 연결해야 합니다.");
            }

            await using var secretStream = File.OpenRead(AppPaths.ClientSecretFile);
            var secrets = await GoogleClientSecrets.FromStreamAsync(secretStream, ct).ConfigureAwait(false);
            _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = secrets.Secrets,
                Scopes = [YouTubeService.Scope.Youtube],
                DataStore = store
            });
            var credential = new UserCredential(_flow, "user", token);
            if (credential.Token.IsStale && !await credential.RefreshTokenAsync(ct).ConfigureAwait(false))
            {
                authHealth.ReportPermanentFailure(YouTubeAuthFailureKind.TokenRejected);
                throw new YouTubeAuthorizationRequiredException("YouTube 로그인이 만료되었습니다.");
            }

            authHealth.ObserveAccessTokenExpiry(YouTubeAccessTokenExpiry.Resolve(credential.Token));
            _service = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Media Capture Helper"
            });
            return _service;
        }
        catch (TokenResponseException ex)
        {
            authHealth.ReportPermanentFailure(YouTubeAuthFailureKind.TokenRejected);
            throw new YouTubeAuthorizationRequiredException("YouTube 로그인이 만료되었습니다.", ex);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            authHealth.ReportPermanentFailure(YouTubeAuthFailureKind.TokenRejected);
            throw new YouTubeAuthorizationRequiredException("YouTube 로그인이 만료되었습니다.", ex);
        }
        finally
        {
            _serviceGate.Release();
        }
    }
}
