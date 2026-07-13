using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// VAPID (RFC 8292) header tests: the JWT is well-formed, signed with the private key, verifiable
/// with the public key, and carries the correct aud/exp/sub claims.
/// </summary>
public class VapidSignerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-vapid-").FullName;
    private readonly VapidKeys _keys;

    public VapidSignerTests() =>
        _keys = new VapidKeyStore(Path.Combine(_dir, "vapid.json"), new Base64Protector()).GetOrCreate();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Header_is_vapid_scheme_with_t_and_k()
    {
        var header = VapidSigner.BuildAuthorizationHeader(
            "https://fcm.googleapis.com", "mailto:me@x.com", _keys, new DateTime(2026, 7, 13, 0, 0, 0));

        Assert.StartsWith("vapid t=", header);
        Assert.Contains(", k=", header);
        Assert.EndsWith(_keys.PublicKeyBase64Url, header);
    }

    [Fact]
    public void Jwt_signature_verifies_against_the_public_key()
    {
        var header = VapidSigner.BuildAuthorizationHeader(
            "https://updates.push.services.mozilla.com", "mailto:me@x.com", _keys,
            new DateTime(2026, 7, 13, 0, 0, 0));
        var jwt = ExtractJwt(header);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64Url.Decode(parts[2]);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = _keys.PublicKey[1..33], Y = _keys.PublicKey[33..65] }
        });
        Assert.True(ecdsa.VerifyData(
            signed, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Claims_carry_audience_subject_and_bounded_expiry()
    {
        var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var header = VapidSigner.BuildAuthorizationHeader(
            "https://fcm.googleapis.com", "mailto:ops@school.kr", _keys, now, TimeSpan.FromHours(12));

        using var doc = JsonDocument.Parse(Base64Url.Decode(ExtractJwt(header).Split('.')[1]));
        var root = doc.RootElement;

        Assert.Equal("https://fcm.googleapis.com", root.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@school.kr", root.GetProperty("sub").GetString());
        var expected = new DateTimeOffset(now).AddHours(12).ToUnixTimeSeconds();
        Assert.Equal(expected, root.GetProperty("exp").GetInt64());
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abcDEF123", "https://fcm.googleapis.com")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/xyz", "https://updates.push.services.mozilla.com")]
    public void AudienceFor_is_the_endpoint_origin(string endpoint, string expected) =>
        Assert.Equal(expected, VapidSigner.AudienceFor(endpoint));

    private static string ExtractJwt(string header) =>
        header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];

    private sealed class Base64Protector : ITokenProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
