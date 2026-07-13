using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Persists the phone browsers subscribed to Web Push (원격 컨트롤러 개선 Phase 3). One operator may
/// enable notifications on several devices, so this is a set keyed by endpoint. The
/// <see cref="WebPushNotifier"/> reads it to fan a message out and prunes entries the push service
/// reports as gone (404/410).
/// </summary>
public interface IPushSubscriptionStore
{
    /// <summary>Every current subscription (snapshot; safe to enumerate without holding a lock).</summary>
    IReadOnlyList<StoredPushSubscription> GetAll();

    /// <summary>Adds a subscription, replacing any existing one with the same endpoint (re-subscribe).</summary>
    void Add(StoredPushSubscription subscription);

    /// <summary>Removes the subscription with this endpoint; returns false if it was not present.</summary>
    bool Remove(string endpoint);
}
