using System.Text.Json.Serialization;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// Wire contract for the phone's Web Push endpoints (원격 컨트롤러 개선 Phase 3), mirroring
/// SplitRemote/QualityRemote: DTOs + validation live in Core so the shape is unit-tested and
/// RemoteControlServer keeps the routes thin. Endpoints (behind the existing remote auth gate):
/// <c>GET /api/push/vapid</c>, <c>POST /api/push/subscribe</c>, <c>DELETE /api/push/subscribe</c>.
/// </summary>
public static class PushRemote
{
    /// <summary>GET /api/push/vapid → the applicationServerKey the browser subscribes with.</summary>
    public sealed record VapidDto(string PublicKey);

    /// <summary>POST /api/push/subscribe body — the browser's PushSubscription.toJSON() shape.</summary>
    public sealed record SubscribeRequest(string? Endpoint, SubscribeKeys? Keys);

    public sealed record SubscribeKeys(
        [property: JsonPropertyName("p256dh")] string? P256dh,
        [property: JsonPropertyName("auth")] string? Auth);

    public sealed record ActionResult(bool Ok, string? Error);

    public static VapidDto GetVapid(IVapidKeyStore keys) => new(keys.GetOrCreate().PublicKeyBase64Url);

    public static ActionResult Subscribe(IPushSubscriptionStore store, SubscribeRequest? request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Endpoint)
            || request.Keys is null
            || string.IsNullOrWhiteSpace(request.Keys.P256dh)
            || string.IsNullOrWhiteSpace(request.Keys.Auth))
        {
            return new ActionResult(false, "구독 정보가 올바르지 않습니다.");
        }
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ActionResult(false, "구독 엔드포인트가 유효한 https URL이 아닙니다.");
        }
        // Reject key material that won't decode to the RFC 8291 sizes, before it can ever fail an encrypt.
        if (!IsValidKey(request.Keys.P256dh!, 65) || !IsValidKey(request.Keys.Auth!, 16))
        {
            return new ActionResult(false, "구독 키 형식이 올바르지 않습니다.");
        }

        store.Add(new StoredPushSubscription(request.Endpoint!, request.Keys.P256dh!, request.Keys.Auth!));
        return new ActionResult(true, null);
    }

    public static ActionResult Unsubscribe(IPushSubscriptionStore store, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ActionResult(false, "endpoint가 필요합니다.");
        }
        var removed = store.Remove(endpoint);
        return new ActionResult(removed, removed ? null : "구독을 찾을 수 없습니다.");
    }

    private static bool IsValidKey(string base64Url, int expectedLength)
    {
        try
        {
            return Base64Url.Decode(base64Url).Length == expectedLength;
        }
        catch
        {
            return false;
        }
    }
}
