using System.Security.Cryptography;
using System.Text;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// RFC 8291 (aes128gcm Web Push) encryption tests. The known-answer test pins the implementation to
/// the RFC §5 worked example byte-for-byte (external correctness / interop proof); the round-trip
/// decrypt exercises the production random-salt/random-ephemeral path and proves the assembled body
/// is parseable and self-consistent.
/// </summary>
public class WebPushEncryptorTests
{
    // ---- RFC 8291 §5 test vector (base64url) ----
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Expected =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLoc" +
        "InmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLV" +
        "WGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Encrypt_reproduces_the_rfc8291_known_answer()
    {
        using var serverKey = WebPushEncryptor.CreateEphemeral(
            Base64Url.Decode(AsPrivate), Base64Url.Decode(AsPublic));

        var body = new WebPushEncryptor().Encrypt(
            Base64Url.Decode(Plaintext), Base64Url.Decode(UaPublic), Base64Url.Decode(AuthSecret),
            Base64Url.Decode(Salt), serverKey);

        Assert.Equal(Expected, Base64Url.Encode(body));
    }

    [Fact]
    public void Random_path_round_trips_through_a_ua_side_decrypt()
    {
        // Stand in for a browser subscription: a fresh UA keypair + random auth secret.
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = ExportPublic(ua);
        var auth = RandomNumberGenerator.GetBytes(16);
        var message = Encoding.UTF8.GetBytes("무음 감지 — 201호 마이크");

        var body = new WebPushEncryptor().Encrypt(
            message, Base64Url.Encode(uaPublic), Base64Url.Encode(auth));

        Assert.Equal(message, DecryptAsUa(body, ua, uaPublic, auth));
    }

    [Fact]
    public void Rejects_a_client_key_that_is_not_65_bytes()
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => new WebPushEncryptor().Encrypt(
            [1, 2, 3], new byte[10], new byte[16], new byte[16], server));
    }

    // ---- UA-side decrypt (RFC 8291, reversed) used only to validate the round trip ----

    private static byte[] DecryptAsUa(byte[] body, ECDiffieHellman ua, byte[] uaPublic, byte[] auth)
    {
        var salt = body[0..16];
        int idlen = body[20];
        var serverPublic = body[21..(21 + idlen)];
        var record = body[(21 + idlen)..];

        using var serverPub = ECDiffieHellman.Create();
        serverPub.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] }
        });
        var ecdhSecret = ua.DeriveRawSecretAgreement(serverPub.PublicKey);

        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, serverPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, auth, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var cipher = record[..^16];
        var tag = record[^16..];
        var padded = new byte[cipher.Length];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Decrypt(nonce, cipher, tag, padded);
        }

        // Strip RFC 8188 padding: trailing zeros then the 0x02 last-record delimiter.
        var end = padded.Length - 1;
        while (end >= 0 && padded[end] == 0)
        {
            end--;
        }
        Assert.Equal(0x02, padded[end]);
        return padded[..end];
    }

    private static byte[] ExportPublic(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var result = new byte[65];
        result[0] = 0x04;
        LeftPad(q.X!, 32).CopyTo(result, 1);
        LeftPad(q.Y!, 32).CopyTo(result, 33);
        return result;
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }
        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
