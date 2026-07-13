using System.Security.Cryptography;

namespace SilentStream.Core.Remote.WebPush;

/// <summary>Creates and validates the NIST P-256 material used by RFC 8292 VAPID.</summary>
public static class VapidKeyMaterial
{
    private static readonly byte[] ValidationPayload = "MediaCaptureHelper VAPID validation"u8.ToArray();

    /// <summary>Creates a new application-server VAPID identity.</summary>
    public static VapidKeys Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        LeftPad(parameters.Q.X!, 32).CopyTo(publicKey, 1);
        LeftPad(parameters.Q.Y!, 32).CopyTo(publicKey, 33);
        return new VapidKeys(publicKey, LeftPad(parameters.D!, 32));
    }

    /// <summary>
    /// Verifies lengths, point shape, curve membership, and that the supplied private scalar
    /// actually signs for the supplied public key. This prevents a bad provisioning document from
    /// silently replacing every browser subscription with unusable material.
    /// </summary>
    public static bool IsValid(VapidKeys? keys)
    {
        if (keys is null || keys.PublicKey is null || keys.PrivateKey is null ||
            keys.PublicKey.Length != 65 || keys.PublicKey[0] != 0x04 || keys.PrivateKey.Length != 32)
        {
            return false;
        }

        try
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = keys.PublicKey[1..33],
                    Y = keys.PublicKey[33..65]
                },
                D = keys.PrivateKey.ToArray()
            };
            using var signer = ECDsa.Create(parameters);
            var signature = signer.SignData(ValidationPayload, HashAlgorithmName.SHA256);
            var publicParameters = signer.ExportParameters(includePrivateParameters: false);
            using var verifier = ECDsa.Create(publicParameters);
            return verifier.VerifyData(ValidationPayload, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
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
}
