using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SilentStream.Core.Remote.WebPush;

/// <summary>
/// Encrypts a Web Push message body per RFC 8291 (Message Encryption for Web Push) using the
/// aes128gcm content coding of RFC 8188. The output is the complete request body a push service
/// expects under <c>Content-Encoding: aes128gcm</c>:
/// <code>salt(16) ‖ rs(4, big-endian) ‖ idlen(1)=65 ‖ server_public(65) ‖ ciphertext ‖ tag(16)</code>
/// Only the browser (holder of the subscription private key) can decrypt it.
///
/// <para>The public <see cref="Encrypt(byte[],string,string)"/> path draws a random salt and a fresh
/// P-256 ephemeral key. The deterministic overload takes both, which makes the RFC 8291 §5 known-answer
/// test reproducible and lets callers pin randomness where they must.</para>
/// </summary>
public sealed class WebPushEncryptor
{
    private const int KeyLength = 65;            // uncompressed P-256 point: 0x04 ‖ X(32) ‖ Y(32)
    private const int CoordinateLength = 32;
    private const int SaltLength = 16;
    private const int TagLength = 16;
    private const int RecordSize = 4096;         // single-record cap; notifications are far smaller

    // Info strings (each terminated by a 0x00 octet, per the RFCs).
    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>Production entry point: random salt + fresh ephemeral key.</summary>
    public byte[] Encrypt(byte[] plaintext, string p256dhBase64Url, string authBase64Url)
    {
        var clientPublic = Base64Url.Decode(p256dhBase64Url);
        var authSecret = Base64Url.Decode(authBase64Url);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, clientPublic, authSecret, salt, ephemeral);
    }

    /// <summary>
    /// Deterministic core (RFC 8291 §3.4). <paramref name="salt"/> is the 16-byte record salt and
    /// <paramref name="serverEphemeral"/> is the application server's ephemeral P-256 key for this message.
    /// </summary>
    public byte[] Encrypt(
        byte[] plaintext, byte[] clientPublicKey, byte[] authSecret, byte[] salt,
        ECDiffieHellman serverEphemeral)
    {
        if (clientPublicKey.Length != KeyLength)
        {
            throw new ArgumentException("클라이언트 공개키는 65바이트 비압축 P-256 점이어야 합니다.", nameof(clientPublicKey));
        }
        if (salt.Length != SaltLength)
        {
            throw new ArgumentException("salt는 16바이트여야 합니다.", nameof(salt));
        }

        // 1) ECDH raw shared secret (shared point x-coordinate, 32 bytes). Keep the client key alive
        //    through the derive call by scoping it here.
        byte[] ecdhSecret;
        using (var clientEcdh = ECDiffieHellman.Create())
        {
            clientEcdh.ImportParameters(PublicParameters(clientPublicKey));
            ecdhSecret = serverEphemeral.DeriveRawSecretAgreement(clientEcdh.PublicKey);
        }

        var serverPublic = ExportPublicKey(serverEphemeral);

        // 2) Combine auth_secret + ecdh_secret into the input keying material (RFC 8291 §3.4):
        //    IKM = HKDF(salt=auth_secret, IKM=ecdh_secret, info="WebPush: info\0" ‖ ua_public ‖ as_public, L=32)
        var keyInfo = Concat(KeyInfoPrefix, clientPublicKey, serverPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        // 3) Content-encryption key + nonce from the record salt (RFC 8188 §2.2).
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, CekInfo);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, NonceInfo);

        // 4) A single (final) record: append the 0x02 padding delimiter, no extra padding (RFC 8291 §3.4).
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[plaintext.Length] = 0x02;
        if (padded.Length + TagLength > RecordSize)
        {
            throw new ArgumentException("Web Push 메시지가 단일 레코드 한도를 초과했습니다.", nameof(plaintext));
        }

        // 5) AES-128-GCM.
        var ciphertext = new byte[padded.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(cek, TagLength))
        {
            aes.Encrypt(nonce, padded, ciphertext, tag);
        }

        // 6) Assemble the aes128gcm body.
        var body = new byte[SaltLength + 4 + 1 + KeyLength + ciphertext.Length + TagLength];
        var span = body.AsSpan();
        salt.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(SaltLength, 4), RecordSize);
        span[SaltLength + 4] = KeyLength;                          // idlen
        serverPublic.CopyTo(span.Slice(SaltLength + 5));           // keyid = as_public
        var rest = span.Slice(SaltLength + 5 + KeyLength);
        ciphertext.CopyTo(rest);
        tag.CopyTo(rest.Slice(ciphertext.Length));
        return body;
    }

    /// <summary>Builds an ECDH key from a known private scalar + public point (test/KAT seam).</summary>
    public static ECDiffieHellman CreateEphemeral(byte[] privateKeyD, byte[] publicKey65)
    {
        var ecdh = ECDiffieHellman.Create();
        var p = PublicParameters(publicKey65);
        p.D = privateKeyD;
        ecdh.ImportParameters(p);
        return ecdh;
    }

    private static ECParameters PublicParameters(byte[] uncompressed) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint
        {
            X = uncompressed[1..(1 + CoordinateLength)],
            Y = uncompressed[(1 + CoordinateLength)..KeyLength]
        }
    };

    private static byte[] ExportPublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var result = new byte[KeyLength];
        result[0] = 0x04;
        LeftPad(q.X!, CoordinateLength).CopyTo(result, 1);
        LeftPad(q.Y!, CoordinateLength).CopyTo(result, 1 + CoordinateLength);
        return result;
    }

    // ECParameters coordinates drop leading zero octets; the wire format needs fixed 32-byte fields.
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
