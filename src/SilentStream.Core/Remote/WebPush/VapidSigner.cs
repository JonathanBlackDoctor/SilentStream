using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// Builds the RFC 8292 VAPID <c>Authorization</c> header: a short-lived ES256 JWT plus the
/// application server public key — <c>vapid t=&lt;jwt&gt;, k=&lt;base64url public key&gt;</c>. The push
/// service verifies the signature against <c>k</c> and checks the JWT <c>aud</c> against its own origin.
/// </summary>
public static class VapidSigner
{
    // {"typ":"JWT","alg":"ES256"} — encoded once; base64url'd per call.
    private static readonly byte[] JwtHeader = Encoding.ASCII.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}");

    /// <summary>Token lifetime; RFC 8292 requires <c>exp</c> no more than 24h past issuance.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);

    public static string BuildAuthorizationHeader(
        string audience, string subject, VapidKeys keys, DateTime utcNow) =>
        BuildAuthorizationHeader(audience, subject, keys, utcNow, DefaultLifetime);

    public static string BuildAuthorizationHeader(
        string audience, string subject, VapidKeys keys, DateTime utcNow, TimeSpan lifetime)
    {
        var exp = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc))
            .Add(lifetime).ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new VapidClaims(audience, exp, subject));
        var signingInput = $"{Base64Url.Encode(JwtHeader)}.{Base64Url.Encode(payload)}";

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = keys.PublicKey[1..33], Y = keys.PublicKey[33..65] },
            D = keys.PrivateKey
        });
        // ES256 wants the raw R‖S fixed-field concatenation, not DER.
        var signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var jwt = $"{signingInput}.{Base64Url.Encode(signature)}";
        return $"vapid t={jwt}, k={keys.PublicKeyBase64Url}";
    }

    /// <summary>The VAPID <c>aud</c> — the push service origin (scheme + authority) of an endpoint.</summary>
    public static string AudienceFor(string endpoint) =>
        new Uri(endpoint).GetLeftPart(UriPartial.Authority);

    private sealed record VapidClaims(
        [property: JsonPropertyName("aud")] string Aud,
        [property: JsonPropertyName("exp")] long Exp,
        [property: JsonPropertyName("sub")] string Sub);
}
