using SilentStream.Core.Implementations;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

public class PushSubscriptionStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-push-").FullName;
    private readonly string _file;

    public PushSubscriptionStoreTests() => _file = Path.Combine(_dir, "push_subscriptions.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static StoredPushSubscription Sub(string endpoint) => new(endpoint, "p256dh", "auth");

    [Fact]
    public void Add_then_GetAll_returns_it()
    {
        var store = new PushSubscriptionStore(_file);
        store.Add(Sub("https://push/1"));

        var only = Assert.Single(store.GetAll());
        Assert.Equal("https://push/1", only.Endpoint);
    }

    [Fact]
    public void Adding_the_same_endpoint_replaces_rather_than_duplicates()
    {
        var store = new PushSubscriptionStore(_file);
        store.Add(new StoredPushSubscription("https://push/1", "old", "old"));
        store.Add(new StoredPushSubscription("https://push/1", "new", "new"));

        var only = Assert.Single(store.GetAll());
        Assert.Equal("new", only.P256dh);
    }

    [Fact]
    public void Remove_reports_presence_and_deletes()
    {
        var store = new PushSubscriptionStore(_file);
        store.Add(Sub("https://push/1"));

        Assert.True(store.Remove("https://push/1"));
        Assert.False(store.Remove("https://push/1")); // already gone
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Subscriptions_survive_a_new_store_instance()
    {
        new PushSubscriptionStore(_file).Add(Sub("https://push/keep"));

        var reloaded = new PushSubscriptionStore(_file).GetAll();

        Assert.Equal("https://push/keep", Assert.Single(reloaded).Endpoint);
    }

    [Fact]
    public void A_corrupt_file_starts_empty_instead_of_throwing()
    {
        File.WriteAllText(_file, "}{ not json");

        Assert.Empty(new PushSubscriptionStore(_file).GetAll());
    }

    [Fact]
    public void Clear_removes_every_subscription_and_persists_the_empty_set()
    {
        var store = new PushSubscriptionStore(_file);
        store.Add(Sub("https://push/1"));
        store.Add(Sub("https://push/2"));

        Assert.Equal(2, store.Clear());
        Assert.Empty(store.GetAll());
        Assert.Empty(new PushSubscriptionStore(_file).GetAll());
    }
}
