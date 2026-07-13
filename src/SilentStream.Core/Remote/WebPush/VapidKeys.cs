namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// The application server's VAPID identity (RFC 8292): a stable NIST P-256 keypair generated once
/// and persisted. The public key (uncompressed point 0x04‖X‖Y, 65 bytes) is the browser's
/// <c>applicationServerKey</c> and the VAPID <c>k=</c> parameter; the private scalar
/// (<see cref="PrivateKey"/>, 32 bytes) signs the auth JWT and is stored DPAPI-encrypted.
/// </summary>
public sealed record VapidKeys(byte[] PublicKey, byte[] PrivateKey)
{
    /// <summary>65-byte uncompressed point as base64url — applicationServerKey / VAPID <c>k=</c>.</summary>
    public string PublicKeyBase64Url => Base64Url.Encode(PublicKey);
}
