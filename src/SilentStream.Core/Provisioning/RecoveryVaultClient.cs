using System.Net.Http.Json;
using System.Text.Json;
using SilentStream.Core.Recovery;

namespace SilentStream.Core.Provisioning;

/// <summary>HTTPS client for the opaque per-device recovery vault and removal command handshake.</summary>
public sealed class RecoveryVaultClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public RecoveryVaultClient(string serviceUrl)
        : this(RecoveryUri.Parse(serviceUrl), new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, true) { }

    public RecoveryVaultClient(Uri serviceUrl, HttpClient http)
        : this(RecoveryUri.Validate(serviceUrl), http, false) { }

    private RecoveryVaultClient(Uri serviceUrl, HttpClient http, bool ownsHttp)
    {
        _baseUri = serviceUrl;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
    }

    public async Task RegisterAsync(RecoveryRegistrationRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/recovery/register"), request, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(RecoverySnapshotUploadRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/recovery/snapshots"), request, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<RecoveryChallenge?> GetChallengeAsync(string keyId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/recovery/challenges"),
            new RecoveryChallengeRequest(keyId), Json, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ChallengeWire>(Json, ct).ConfigureAwait(false)
            ?? throw new RecoveryVaultException("Recovery challenge response is invalid.");
        return new RecoveryChallenge(body.ChallengeId, Convert.FromBase64String(body.Nonce), body.ExpiresAtUtc);
    }

    public async Task<RecoverySnapshot?> RestoreAsync(RecoveryChallenge challenge, byte[] signature, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/recovery/restore"),
            new RecoveryRestoreRequest(challenge.ChallengeId, Convert.ToBase64String(signature)), Json, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RecoverySnapshot>(Json, ct).ConfigureAwait(false)
            ?? throw new RecoveryVaultException("Recovery snapshot response is invalid.");
    }

    public async Task<RemoteRemovalAuthorization> ConsumeRemovalAsync(
        string roomId, string installationId, string tunnelToken, string commandId, CancellationToken ct = default)
    {
        var body = new { roomId, installationId, tunnelToken };
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, $"api/device-removal-commands/{Uri.EscapeDataString(commandId)}/consume"), body, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var wire = await response.Content.ReadFromJsonAsync<RemovalAuthorizationWire>(Json, ct).ConfigureAwait(false)
            ?? throw new RecoveryVaultException("Removal authorization response is invalid.");
        return new RemoteRemovalAuthorization(wire.CompletionToken);
    }

    public async Task<RemoteRemovalCommand> IssueRemovalAsync(string roomId, string administratorToken,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/admin/removal-commands"))
        {
            Content = JsonContent.Create(new { roomId }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("X-Admin-Token", administratorToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var wire = await response.Content.ReadFromJsonAsync<RemovalCommandWire>(Json, ct).ConfigureAwait(false)
            ?? throw new RecoveryVaultException("Removal command response is invalid.");
        return new RemoteRemovalCommand(wire.CommandId);
    }

    public async Task CompleteRemovalAsync(string commandId, string completionToken, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, $"api/device-removal-commands/{Uri.EscapeDataString(commandId)}/complete"),
            new { completionToken }, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorWire>(Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error)) throw new RecoveryVaultException(error.Error);
        }
        catch (JsonException) { }
        throw new RecoveryVaultException($"Recovery service request failed ({(int)response.StatusCode}).");
    }

    private sealed record ChallengeWire(string ChallengeId, string Nonce, DateTimeOffset ExpiresAtUtc);
    private sealed record RemovalCommandWire(string CommandId);
    private sealed record RemovalAuthorizationWire(string CompletionToken);
    private sealed record ErrorWire(string? Error);
}

public sealed class RecoveryVaultException(string message) : Exception(message);

internal static class RecoveryUri
{
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new ArgumentException("Recovery service URL is invalid.", nameof(value));
        return Validate(uri);
    }

    public static Uri Validate(Uri uri)
    {
        var allowed = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);
        if (!allowed || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Recovery service must use HTTPS (except loopback development).", nameof(uri));
        return new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}
