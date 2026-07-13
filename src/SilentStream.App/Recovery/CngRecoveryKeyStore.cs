using System.Security.Cryptography;
using SilentStream.Core.Recovery;

namespace SilentStream.App.Recovery;

/// <summary>
/// A named non-exportable RSA key that survives removing the Velopack package and AppData. TPM is
/// preferred; a Windows software CNG key is a deliberate compatibility fallback for PCs without
/// the Microsoft Platform Crypto Provider.
/// </summary>
public sealed class CngRecoveryKeyStore : IRecoveryKeyStore
{
    private const string KeyName = "MediaCaptureHelper.Recovery.v1";

    public RecoveryIdentity GetOrCreate()
    {
        using var rsa = OpenRsa(create: true) ?? throw new CryptographicException("Windows recovery key is unavailable.");
        return Identity(rsa);
    }

    public RecoveryIdentity? TryOpen()
    {
        using var rsa = OpenRsa(create: false);
        return rsa is null ? null : Identity(rsa);
    }

    public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey)
    {
        using var rsa = OpenRsa(create: false) ?? throw new CryptographicException("Recovery key was not found.");
        return rsa.Encrypt(dataKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
    }

    public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey)
    {
        using var rsa = OpenRsa(create: false) ?? throw new CryptographicException("Recovery key was not found.");
        return rsa.Decrypt(wrappedDataKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var rsa = OpenRsa(create: false) ?? throw new CryptographicException("Recovery key was not found.");
        return rsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static RSACng? OpenRsa(bool create)
    {
        var platform = TryOpen(CngProvider.MicrosoftPlatformCryptoProvider);
        if (platform is not null) return platform;
        var software = TryOpen(CngProvider.MicrosoftSoftwareKeyStorageProvider);
        if (software is not null || !create) return software;

        try
        {
            return Create(CngProvider.MicrosoftPlatformCryptoProvider);
        }
        catch (CryptographicException)
        {
            return Create(CngProvider.MicrosoftSoftwareKeyStorageProvider);
        }
    }

    private static RSACng? TryOpen(CngProvider provider)
    {
        try { return new RSACng(CngKey.Open(KeyName, provider)); }
        catch (CryptographicException) { return null; }
    }

    private static RSACng Create(CngProvider provider)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            KeyUsage = CngKeyUsages.Decryption | CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None
        };
        return new RSACng(CngKey.Create(CngAlgorithm.Rsa, KeyName, parameters));
    }

    private static RecoveryIdentity Identity(RSA rsa)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var id = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
        var protection = rsa is RSACng cng && cng.Key.Provider == CngProvider.MicrosoftPlatformCryptoProvider
            ? "tpm"
            : "windows";
        return new RecoveryIdentity(id, Convert.ToBase64String(spki), protection);
    }
}
