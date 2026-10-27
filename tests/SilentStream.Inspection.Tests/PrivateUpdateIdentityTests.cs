using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SilentStream.Inspection.Installer;
using Xunit;

namespace SilentStream.Inspection.Tests;
public sealed class PrivateUpdateIdentityTests
{
    [Fact]
    public void Proof_binds_the_method_full_target_time_and_unique_nonce_to_a_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = new PrivateUpdateIdentity(key);
        var uri = new Uri(PrivateUpdateIdentity.BaseUrl + "/releases.win.json?channel=win");
        using var request = identity.Request(HttpMethod.Get, uri);
        string Header(string name) => request.Headers.GetValues(name).Single();
        var nonce = Header("X-Update-Nonce");
        var canonical = string.Join("\n", "GET", uri.PathAndQuery, Header("X-Update-Time"), nonce);
        var signature = Header("X-Update-Signature").Replace('-', '+').Replace('_', '/') + "==";
        Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(canonical), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(key.VerifyData(Encoding.UTF8.GetBytes(canonical + "tamper"), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var second = identity.Request(HttpMethod.Get, uri);
        Assert.NotEqual(nonce, second.Headers.GetValues("X-Update-Nonce").Single());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant(), identity.KeyId);
    }
    [Theory]
    [InlineData("http://agent.silentstream.win/api/updates/releases.win.json")]
    [InlineData("https://other.invalid/api/updates/releases.win.json")]
    [InlineData("https://agent.silentstream.win/api/device/self")]
    public void Identity_does_not_sign_unrelated_destinations(string url)
    {
        using var identity = new PrivateUpdateIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        Assert.Throws<InvalidDataException>(() => identity.Request(HttpMethod.Get, new Uri(url)));
    }
}
