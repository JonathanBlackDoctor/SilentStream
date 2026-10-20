using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.UsbDocuments;

/// <summary>Independent DPAPI credential; background work never opens an authorization browser.</summary>
public sealed class GoogleDocumentDriveClient : IDocumentDriveClient, IDisposable
{
    // Existing folders created in Drive's website need Drive access; drive.file cannot see them.
    // This consent is explicit and independent of YouTube. No sharing API is used.
    private const string Scope = "https://www.googleapis.com/auth/drive";
    private readonly ITokenProtector _protector;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>>? _accessToken;
    private UserCredential? _credential;
    private readonly string _tokenFile = Path.Combine(AppPaths.AppDataDir, "document-drive-token.dat");

    public GoogleDocumentDriveClient(ITokenProtector protector) : this(protector, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })) { }
    public GoogleDocumentDriveClient(ITokenProtector protector, HttpClient http, Func<CancellationToken, Task<string>>? accessToken = null)
    { _protector = protector; _http = http; _accessToken = accessToken; _http.Timeout = Timeout.InfiniteTimeSpan; }

    public async Task ConnectAsync(DocumentDriveConfig settings, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.ClientSecretFile)) throw new DriveDocumentException("PC의 Google 클라이언트 설정을 확인하세요.");
        await using var source = File.OpenRead(AppPaths.ClientSecretFile);
        var secrets = await GoogleClientSecrets.FromStreamAsync(source, ct).ConfigureAwait(false);
        var store = new EncryptedFileTokenDataStore(_tokenFile, _protector);
        // Explicit reconnect allows switching away from a previously selected personal account.
        await store.ClearAsync().ConfigureAwait(false);
        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(secrets.Secrets, [Scope], "documents", ct, store).ConfigureAwait(false);
        try { await ValidateAsync(settings, ct).ConfigureAwait(false); }
        catch { _credential = null; await store.ClearAsync().ConfigureAwait(false); throw; }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null) return await _accessToken(ct).ConfigureAwait(false);
        if (_credential is null)
        {
            var store = new EncryptedFileTokenDataStore(_tokenFile, _protector);
            var token = await store.GetAsync<TokenResponse>("documents").ConfigureAwait(false);
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken) || !File.Exists(AppPaths.ClientSecretFile))
                throw new DriveDocumentException("PC 제어창에서 Drive 계정을 연결하세요.");
            await using var source = File.OpenRead(AppPaths.ClientSecretFile);
            var secrets = await GoogleClientSecrets.FromStreamAsync(source, ct).ConfigureAwait(false);
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            { ClientSecrets = secrets.Secrets, Scopes = [Scope], DataStore = store });
            _credential = new UserCredential(flow, "documents", token);
        }
        return await _credential.GetAccessTokenForRequestAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new DriveDocumentException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Drive 권한·API 사용 설정을 확인하고 계정을 다시 연결하세요."
                : "Drive 요청 실패. 네트워크·폴더·저장용량을 확인하고 재시도하세요.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task ValidateAsync(DocumentDriveConfig settings, CancellationToken ct)
    {
        using var about = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", ct).ConfigureAwait(false);
        var identity = await ReadAsync(about, ct).ConfigureAwait(false);
        if (!identity.GetProperty("user").GetProperty("emailAddress").GetString()!.Equals(settings.Account, StringComparison.OrdinalIgnoreCase))
            throw new DriveDocumentException("로그인 계정이 지정한 업로드 계정과 다릅니다. 올바른 계정으로 다시 연결하세요.");
        using var folder = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(settings.FolderId)
            + "?fields=id,mimeType,trashed,owners(emailAddress),capabilities(canAddChildren)", ct).ConfigureAwait(false);
        var target = await ReadAsync(folder, ct).ConfigureAwait(false);
        if (target.GetProperty("mimeType").GetString() != "application/vnd.google-apps.folder"
            || target.GetProperty("trashed").GetBoolean()
            || !target.GetProperty("capabilities").GetProperty("canAddChildren").GetBoolean()
            || !target.GetProperty("owners").EnumerateArray().Any(x => string.Equals(x.GetProperty("emailAddress").GetString(), settings.Account, StringComparison.OrdinalIgnoreCase)))
            throw new DriveDocumentException("지정 계정이 소유하고 업로드할 수 있는 Drive 폴더를 선택하세요.");
    }

    public async Task<string> AllocateIdAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files", ct).ConfigureAwait(false);
        var data = await ReadAsync(response, ct).ConfigureAwait(false);
        return data.GetProperty("ids")[0].GetString() ?? throw new DriveDocumentException("Drive 파일 번호를 받지 못했습니다.");
    }

    public async Task UploadAsync(DocumentDriveConfig settings, UsbDocumentInfo file, Stream content,
        string remoteId, string md5, CancellationToken ct)
    {
        var metadataUrl = "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(remoteId)
            + "?fields=id,size,md5Checksum,parents,trashed,appProperties";
        using var existing = await SendAsync(HttpMethod.Get, metadataUrl, ct).ConfigureAwait(false);
        if (existing.StatusCode != HttpStatusCode.NotFound)
        { Verify(await ReadAsync(existing, ct).ConfigureAwait(false), settings, file, remoteId, md5); return; }

        var type = Path.GetExtension(file.Name).ToLowerInvariant() switch
        { ".pdf" => "application/pdf", ".ppt" => "application/vnd.ms-powerpoint", _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation" };
        using var metadata = JsonContent.Create(new { id = remoteId, name = file.Date + " " + file.Name, mimeType = type,
            parents = new[] { settings.FolderId }, appProperties = new { silentStreamSha256 = file.Sha256 } });
        using var begin = await SendAsync(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable", ct, metadata).ConfigureAwait(false);
        if (!begin.IsSuccessStatusCode) { await ReadAsync(begin, ct).ConfigureAwait(false); }
        var location = begin.Headers.Location;
        if (location is null || location.Scheme != "https" || location.Host != "www.googleapis.com"
            || !location.AbsolutePath.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal))
            throw new DriveDocumentException("Drive 업로드 주소를 확인하지 못했습니다.");
        using var body = new StreamContent(new ThrottledStream(content, 1_000_000));
        body.Headers.ContentType = new MediaTypeHeaderValue(type);
        body.Headers.ContentLength = file.Size;
        using var uploaded = await SendAsync(HttpMethod.Put, location.AbsoluteUri, ct, body).ConfigureAwait(false);
        if (!uploaded.IsSuccessStatusCode) { await ReadAsync(uploaded, ct).ConfigureAwait(false); }
        using var readback = await SendAsync(HttpMethod.Get, metadataUrl, ct).ConfigureAwait(false);
        Verify(await ReadAsync(readback, ct).ConfigureAwait(false), settings, file, remoteId, md5);
    }

    private static void Verify(JsonElement data, DocumentDriveConfig settings, UsbDocumentInfo file, string id, string md5)
    {
        if (data.GetProperty("id").GetString() != id || data.GetProperty("trashed").GetBoolean()
            || data.GetProperty("size").GetString() != file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !string.Equals(data.GetProperty("md5Checksum").GetString(), md5, StringComparison.OrdinalIgnoreCase)
            || !data.GetProperty("parents").EnumerateArray().Any(x => x.GetString() == settings.FolderId)
            || data.GetProperty("appProperties").GetProperty("silentStreamSha256").GetString() != file.Sha256)
            throw new DriveDocumentException("Drive 업로드 검증 실패. 파일 크기·내용·대상 폴더를 확인하세요.");
    }

    public void Dispose() { _http.Dispose(); _credential?.Flow.Dispose(); }
}
