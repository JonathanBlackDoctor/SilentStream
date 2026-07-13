using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentStream.Core.Contracts;
using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Web Push transport (원격 컨트롤러 개선 Phase 3): fans one message out to every subscribed phone
/// browser — body encrypted per RFC 8291, request authorized per RFC 8292. Sits beside
/// <see cref="TelegramNotifier"/> on the <see cref="INotifier"/> fan-out; the two are complementary
/// (텔레그램 covers LAN/plain-http where service workers can't run). Subscriptions the push service
/// reports as gone (404/410) are pruned. Like every notifier it logs and returns false on failure,
/// never throwing into the health pipeline. The HttpClient + clock are injectable for offline tests.
/// </summary>
public sealed class WebPushNotifier : INotifier, IDisposable
{
    private const string Subject = "mailto:silentstream@localhost";
    private static readonly int TtlSeconds = (int)TimeSpan.FromHours(12).TotalSeconds;

    private readonly IPushSubscriptionStore _subscriptions;
    private readonly IVapidKeyStore _vapidKeys;
    private readonly WebPushEncryptor _encryptor = new();
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Production constructor (DI selects this — HttpClient is not registered).</summary>
    public WebPushNotifier(IPushSubscriptionStore subscriptions, IVapidKeyStore vapidKeys, ILogService log)
        : this(subscriptions, vapidKeys, log,
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, () => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam: stub the push service via HttpClient and pin the clock.</summary>
    public WebPushNotifier(
        IPushSubscriptionStore subscriptions, IVapidKeyStore vapidKeys, ILogService log,
        HttpClient http, Func<DateTime> utcNow)
    {
        _subscriptions = subscriptions;
        _vapidKeys = vapidKeys;
        _log = log;
        _http = http;
        _utcNow = utcNow;
    }

    public async Task<bool> SendAsync(string message, CancellationToken ct)
    {
        var subs = _subscriptions.GetAll();
        if (subs.Count == 0)
        {
            return false; // no phone opted in — quiet no-op, mirrors TelegramNotifier's unconfigured path
        }

        VapidKeys keys;
        try
        {
            keys = _vapidKeys.GetOrCreate();
        }
        catch (Exception ex)
        {
            _log.Warn($"VAPID 키 로드 실패 — 웹푸시를 건너뜁니다: {ex.Message}");
            return false;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new PushPayload("Media Capture Helper", message));
        var delivered = false;
        foreach (var sub in subs)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            delivered |= await SendToOneAsync(sub, payload, keys, ct).ConfigureAwait(false);
        }
        return delivered;
    }

    private async Task<bool> SendToOneAsync(
        StoredPushSubscription sub, byte[] payload, VapidKeys keys, CancellationToken ct)
    {
        try
        {
            var body = _encryptor.Encrypt(payload, sub.P256dh, sub.Auth);
            using var request = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint)
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", TtlSeconds.ToString());
            request.Headers.TryAddWithoutValidation("Urgency", "normal");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                VapidSigner.BuildAuthorizationHeader(
                    VapidSigner.AudienceFor(sub.Endpoint), Subject, keys, _utcNow()));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _subscriptions.Remove(sub.Endpoint);
                _log.Info("만료된 웹푸시 구독을 제거했습니다.");
            }
            else
            {
                _log.Warn($"웹푸시 전송 실패: HTTP {(int)response.StatusCode}");
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // shutdown or timeout
        }
        catch (Exception ex)
        {
            _log.Warn($"웹푸시 전송 오류: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record PushPayload(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body);
}
