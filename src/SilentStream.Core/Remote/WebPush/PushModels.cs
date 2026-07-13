namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// One browser Web Push subscription at rest — the RFC 8291 keying material the app needs to
/// encrypt a message for that device. Keyed by <see cref="Endpoint"/> (the push service URL the
/// browser handed us); <see cref="P256dh"/>/<see cref="Auth"/> are base64url as delivered by
/// <c>PushSubscription.toJSON()</c>.
/// </summary>
public sealed record StoredPushSubscription(string Endpoint, string P256dh, string Auth);
