using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Provisioning;

/// <summary>
/// Non-secret bootstrap data placed next to the installed executable as
/// <c>provisioning.json</c>. The file deliberately contains only a HTTPS endpoint — never a
/// Cloudflare tunnel token, enrollment code, or server administrator credential.
/// </summary>
public sealed class RoomProvisioningBootstrap
{
    public string ServiceUrl { get; set; } = string.Empty;
}

/// <summary>One selectable room returned by the provisioning service. No secret fields appear here.</summary>
public sealed record ProvisioningRoom(string Id, string Name);

/// <summary>Room-picker data and the server's current enrollment policy.</summary>
public sealed record ProvisioningCatalog(bool RequiresActivationCode, IReadOnlyList<ProvisioningRoom> Rooms);

/// <summary>Input sent when a freshly installed PC claims a room.</summary>
public sealed record ProvisioningClaimRequest(
    string RoomId,
    string InstallationId,
    string MachineName,
    string? ActivationCode);

/// <summary>
/// The shared VAPID identity returned only by the trusted provisioning service. It stays in
/// memory until the host installs it into the separate DPAPI-protected VAPID store.
/// </summary>
public sealed record ProvisioningVapidKey(string PublicKey, string PrivateKey)
{
    public bool TryGetKeys(out VapidKeys keys)
    {
        if (string.IsNullOrWhiteSpace(PublicKey) || string.IsNullOrWhiteSpace(PrivateKey))
        {
            keys = null!;
            return false;
        }
        try
        {
            keys = new VapidKeys(Base64Url.Decode(PublicKey), Base64Url.Decode(PrivateKey));
            return VapidKeyMaterial.IsValid(keys);
        }
        catch (FormatException)
        {
            keys = null!;
            return false;
        }
    }
}

/// <summary>
/// The single-room assignment returned over HTTPS. It exists only in memory until the WPF host
/// immediately DPAPI-protects <see cref="CloudflareTunnelToken"/> before writing config.json.
/// </summary>
public sealed record ProvisioningAssignment(
    string RoomId,
    string DisplayName,
    string CloudflareHostname,
    string CloudflareTunnelToken,
    int Port,
    string CloudflareProtocol,
    ProvisioningVapidKey? SharedVapid);

/// <summary>
/// Proof used by an already-provisioned installation to retrieve only the shared VAPID key.
/// The caller takes the per-room tunnel token from its DPAPI-protected local configuration.
/// </summary>
public sealed record ProvisioningVapidRefreshRequest(string RoomId, string InstallationId, string TunnelToken);

/// <summary>Server-reported provisioning error suitable for a concise setup dialog.</summary>
public sealed class RoomProvisioningException : Exception
{
    public RoomProvisioningException(string message) : base(message) { }
}

/// <summary>
/// HTTPS client for the optional first-install room provisioning service. Keeping this in Core
/// makes its JSON contract unit-testable without WPF or a live service. HTTP is accepted only for
/// loopback development, so a production installer cannot accidentally send a tunnel token over
/// a plain network connection.
/// </summary>
public sealed class RoomProvisioningClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public RoomProvisioningClient(string serviceUrl)
        : this(ParseAndValidateServiceUrl(serviceUrl), new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, true)
    {
    }

    /// <summary>Test seam: supply a stubbed <see cref="HttpClient"/> without transferring ownership.</summary>
    public RoomProvisioningClient(Uri serviceUrl, HttpClient http)
        : this(ValidateServiceUrl(serviceUrl), http, false)
    {
    }

    private RoomProvisioningClient(Uri serviceUrl, HttpClient http, bool ownsHttpClient)
    {
        _baseUri = serviceUrl;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<ProvisioningCatalog> GetCatalogAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(new Uri(_baseUri, "api/rooms"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
        }

        var payload = await response.Content.ReadFromJsonAsync<CatalogResponse>(Json, ct).ConfigureAwait(false);
        var rooms = payload?.Rooms?
            .Where(r => IsValidId(r.Id) && !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new ProvisioningRoom(r.Id!, r.Name!.Trim()))
            .OrderBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

        if (rooms is not { Count: > 0 })
        {
            throw new RoomProvisioningException("등록할 호실 목록을 받지 못했습니다.");
        }
        return new ProvisioningCatalog(payload!.RequiresActivationCode, rooms);
    }

    public async Task<ProvisioningAssignment> ClaimAsync(ProvisioningClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidId(request.RoomId) || string.IsNullOrWhiteSpace(request.InstallationId) ||
            string.IsNullOrWhiteSpace(request.MachineName))
        {
            throw new ArgumentException("호실 또는 설치 식별자가 올바르지 않습니다.", nameof(request));
        }

        var body = new ClaimWireRequest(
            request.RoomId.Trim(), request.InstallationId.Trim(), request.MachineName.Trim(),
            string.IsNullOrWhiteSpace(request.ActivationCode) ? null : request.ActivationCode.Trim());
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/claims"), body, Json, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
        }

        var assignment = await response.Content.ReadFromJsonAsync<AssignmentWireResponse>(Json, ct).ConfigureAwait(false);
        if (assignment is null || !IsValidId(assignment.RoomId) || string.IsNullOrWhiteSpace(assignment.DisplayName) ||
            !IsValidHostname(assignment.CloudflareHostname) || string.IsNullOrWhiteSpace(assignment.CloudflareTunnelToken))
        {
            throw new RoomProvisioningException("서버가 받은 호실 설정이 올바르지 않습니다.");
        }

        var port = assignment.Port is >= 1 and <= 65535 ? assignment.Port : 8787;
        var protocol = assignment.CloudflareProtocol?.ToLowerInvariant() switch
        {
            "quic" => "quic",
            "auto" => "auto",
            _ => "http2"
        };
        var sharedVapid = ParseSharedVapid(assignment.SharedVapid);
        return new ProvisioningAssignment(
            assignment.RoomId!, assignment.DisplayName!.Trim(), assignment.CloudflareHostname!.Trim(),
            assignment.CloudflareTunnelToken!, port, protocol, sharedVapid);
    }

    /// <summary>
    /// Retrieves the optional shared VAPID identity for an installation that was provisioned by
    /// an older release. The refresh request intentionally proves possession of the room's tunnel
    /// token instead of reopening the enrollment-code flow on every update.
    /// </summary>
    public async Task<ProvisioningVapidKey?> RefreshSharedVapidAsync(
        ProvisioningVapidRefreshRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidId(request.RoomId) || string.IsNullOrWhiteSpace(request.InstallationId) ||
            !IsSafeSecret(request.TunnelToken))
        {
            throw new ArgumentException("호실 또는 설치 식별자가 올바르지 않습니다.", nameof(request));
        }

        var body = new VapidRefreshWireRequest(
            request.RoomId.Trim(), request.InstallationId.Trim(), request.TunnelToken);
        using var response = await _http.PostAsJsonAsync(new Uri(_baseUri, "api/assignments/refresh-vapid"), body, Json, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null; // server has not enabled shared multi-room push yet
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
        }

        var payload = await response.Content.ReadFromJsonAsync<VapidRefreshWireResponse>(Json, ct).ConfigureAwait(false);
        return ParseSharedVapid(payload?.SharedVapid)
            ?? throw new RoomProvisioningException("서버가 받은 공유 알림 키가 올바르지 않습니다.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private static async Task<RoomProvisioningException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return new RoomProvisioningException(error.Error.Trim());
            }
        }
        catch (JsonException)
        {
            // Fall through to a status-based message.
        }
        return new RoomProvisioningException($"호실 설정 서버 연결 실패 ({(int)response.StatusCode}).");
    }

    private static Uri ParseAndValidateServiceUrl(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("호실 설정 서버 주소가 올바르지 않습니다.", nameof(serviceUrl));
        }
        return ValidateServiceUrl(uri);
    }

    private static Uri ValidateServiceUrl(Uri uri)
    {
        var allowed = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                      (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);
        if (!allowed || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("호실 설정 서버는 HTTPS 주소여야 합니다(개발용 localhost 제외).", nameof(uri));
        }
        var absolute = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new Uri(absolute, UriKind.Absolute);
    }

    private static bool IsValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsValidHostname(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 253 &&
        Uri.CheckHostName(value.Trim()) is UriHostNameType.Dns;

    private static bool IsSafeSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && value.All(c => !char.IsControl(c));

    private static ProvisioningVapidKey? ParseSharedVapid(SharedVapidWire? wire)
    {
        if (wire is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(wire.PublicKey) || string.IsNullOrWhiteSpace(wire.PrivateKey))
        {
            throw new RoomProvisioningException("서버가 받은 공유 알림 키가 올바르지 않습니다.");
        }

        var key = new ProvisioningVapidKey(wire.PublicKey, wire.PrivateKey);
        if (!key.TryGetKeys(out _))
        {
            throw new RoomProvisioningException("서버가 받은 공유 알림 키가 올바르지 않습니다.");
        }
        return key;
    }

    private sealed record CatalogResponse(bool RequiresActivationCode, List<RoomWire>? Rooms);

    private sealed record RoomWire(string? Id, string? Name);

    private sealed record ClaimWireRequest(string RoomId, string InstallationId, string MachineName, string? ActivationCode);

    private sealed record AssignmentWireResponse(
        string? RoomId,
        string? DisplayName,
        string? CloudflareHostname,
        string? CloudflareTunnelToken,
        int Port,
        string? CloudflareProtocol,
        SharedVapidWire? SharedVapid);

    private sealed record SharedVapidWire(string? PublicKey, string? PrivateKey);

    private sealed record VapidRefreshWireRequest(string RoomId, string InstallationId, string TunnelToken);

    private sealed record VapidRefreshWireResponse(SharedVapidWire? SharedVapid);

    private sealed record ErrorResponse(string? Error);
}
