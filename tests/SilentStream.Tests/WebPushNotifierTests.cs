using System.Net;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// WebPushNotifier tests: the push service is stubbed offline. They assert the RFC 8030/8291/8292
/// wire shape (Content-Encoding, TTL, VAPID Authorization, encrypted body), dead-subscription pruning,
/// and that failures return false instead of throwing into the health pipeline.
/// </summary>
public class WebPushNotifierTests
{
    private const string P256dh =
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Endpoint = "https://push.example.com/device/abc";

    private readonly MemStore _store = new();
    private readonly FakeHandler _handler = new();

    private static readonly VapidKeys Keys = new(
        Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"),
        Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"));

    private WebPushNotifier Create() => new(
        _store, new FixedKeys(Keys), new LogService(), new HttpClient(_handler),
        () => new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task No_subscriptions_sends_nothing()
    {
        Assert.False(await Create().SendAsync("무음 감지", CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Sends_an_encrypted_body_with_vapid_and_aes128gcm_headers()
    {
        _store.Add(new StoredPushSubscription(Endpoint, P256dh, Auth));

        var ok = await Create().SendAsync("무음 감지 — 201호", CancellationToken.None);

        Assert.True(ok);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Endpoint, request.Uri);
        Assert.Contains("aes128gcm", request.ContentEncoding);
        Assert.StartsWith("vapid t=", request.Authorization);
        Assert.Equal("43200", request.Ttl);           // 12h in seconds
        Assert.True(request.BodyLength > 86);           // salt+rs+idlen+key(65) header, plus ciphertext+tag
    }

    [Fact]
    public async Task A_gone_subscription_is_pruned()
    {
        _store.Add(new StoredPushSubscription(Endpoint, P256dh, Auth));
        _handler.StatusCode = HttpStatusCode.Gone;

        var ok = await Create().SendAsync("x", CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(_store.GetAll()); // 410 → removed
    }

    [Fact]
    public async Task A_transient_error_keeps_the_subscription_and_returns_false()
    {
        _store.Add(new StoredPushSubscription(Endpoint, P256dh, Auth));
        _handler.StatusCode = HttpStatusCode.InternalServerError;

        Assert.False(await Create().SendAsync("x", CancellationToken.None));
        Assert.Single(_store.GetAll()); // 500 is not a prune signal
    }

    [Fact]
    public async Task A_network_exception_returns_false_instead_of_throwing()
    {
        _store.Add(new StoredPushSubscription(Endpoint, P256dh, Auth));
        _handler.Throw = new HttpRequestException("연결 실패");

        Assert.False(await Create().SendAsync("x", CancellationToken.None));
    }

    // ---- fakes ----

    private sealed record Captured(string Uri, string ContentEncoding, string Authorization, string Ttl, int BodyLength);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];
        public HttpStatusCode StatusCode = HttpStatusCode.Created;
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentEncoding = request.Content?.Headers.ContentEncoding.FirstOrDefault() ?? "";
            var auth = request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : "";
            var ttl = request.Headers.TryGetValues("TTL", out var t) ? string.Join(",", t) : "";
            lock (Requests)
            {
                Requests.Add(new Captured(request.RequestUri!.ToString(), contentEncoding, auth, ttl, body.Length));
            }
            return new HttpResponseMessage(StatusCode);
        }
    }

    private sealed class MemStore : IPushSubscriptionStore
    {
        private readonly List<StoredPushSubscription> _items = [];
        public IReadOnlyList<StoredPushSubscription> GetAll() => _items.ToList();
        public void Add(StoredPushSubscription s)
        {
            _items.RemoveAll(x => x.Endpoint == s.Endpoint);
            _items.Add(s);
        }
        public bool Remove(string endpoint) => _items.RemoveAll(x => x.Endpoint == endpoint) > 0;
    }

    private sealed class FixedKeys(VapidKeys keys) : IVapidKeyStore
    {
        public VapidKeys GetOrCreate() => keys;
    }
}
