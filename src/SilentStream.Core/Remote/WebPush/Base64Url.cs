namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// Base64url (RFC 4648 §5, no padding) — the wire format for every Web Push key, the browser's
/// PushSubscription fields (endpoint keys), and the VAPID JWT segments. Encoding strips '=' padding
/// and swaps the URL-unsafe alphabet; decoding restores both before delegating to <see cref="Convert"/>.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        // Restore the padding base64 requires (input length rounded up to a multiple of 4).
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
