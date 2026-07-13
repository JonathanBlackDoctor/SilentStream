using SilentStream.Core.Contracts;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

public class PushRemoteTests
{
    // Valid P-256 point (65B) + auth secret (16B) from the RFC 8291 §5 vector.
    private const string ValidP256dh =
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string ValidAuth = "BTBZMqHH6r4Tts7J_aSIgg";

    private readonly MemStore _store = new();

    [Fact]
    public void GetVapid_returns_the_public_key()
    {
        var keys = new VapidKeys(
            Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"),
            Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"));

        Assert.Equal(keys.PublicKeyBase64Url, PushRemote.GetVapid(new FixedKeys(keys)).PublicKey);
    }

    [Fact]
    public void Subscribe_stores_a_valid_subscription()
    {
        var result = PushRemote.Subscribe(_store, new PushRemote.SubscribeRequest(
            "https://push.example.com/abc", new PushRemote.SubscribeKeys(ValidP256dh, ValidAuth)));

        Assert.True(result.Ok);
        Assert.Equal("https://push.example.com/abc", Assert.Single(_store.GetAll()).Endpoint);
    }

    [Fact]
    public void Subscribe_rejects_a_non_https_endpoint()
    {
        var result = PushRemote.Subscribe(_store, new PushRemote.SubscribeRequest(
            "http://push.example.com/abc", new PushRemote.SubscribeKeys(ValidP256dh, ValidAuth)));

        Assert.False(result.Ok);
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void Subscribe_rejects_missing_keys()
    {
        var result = PushRemote.Subscribe(_store,
            new PushRemote.SubscribeRequest("https://push.example.com/abc", null));

        Assert.False(result.Ok);
    }

    [Fact]
    public void Subscribe_rejects_key_material_of_the_wrong_size()
    {
        // p256dh that decodes to fewer than 65 bytes must not be accepted.
        var result = PushRemote.Subscribe(_store, new PushRemote.SubscribeRequest(
            "https://push.example.com/abc", new PushRemote.SubscribeKeys("c2hvcnQ", ValidAuth)));

        Assert.False(result.Ok);
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void Unsubscribe_removes_then_reports_missing()
    {
        PushRemote.Subscribe(_store, new PushRemote.SubscribeRequest(
            "https://push.example.com/abc", new PushRemote.SubscribeKeys(ValidP256dh, ValidAuth)));

        Assert.True(PushRemote.Unsubscribe(_store, "https://push.example.com/abc").Ok);
        Assert.False(PushRemote.Unsubscribe(_store, "https://push.example.com/abc").Ok);
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
