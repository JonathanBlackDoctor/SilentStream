using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Generates the application server's VAPID keypair once and persists it (원격 컨트롤러 개선 Phase 3).
/// The private scalar is DPAPI-encrypted at rest via <see cref="ITokenProtector"/> — same guarantee as
/// the OAuth/텔레그램 secrets. The keypair must stay stable: a new public key silently invalidates every
/// browser subscription, so the persisted key is reused for the life of the install.
/// </summary>
public sealed class VapidKeyStore : IVapidKeyStore
{
    private readonly string _keyFile;
    private readonly ITokenProtector _protector;
    private readonly object _gate = new();
    private VapidKeys? _cached;

    public VapidKeyStore(string keyFile, ITokenProtector protector)
    {
        _keyFile = keyFile;
        _protector = protector;
    }

    public VapidKeys GetOrCreate()
    {
        lock (_gate)
        {
            return _cached ??= Load() ?? Generate();
        }
    }

    private VapidKeys? Load()
    {
        if (!File.Exists(_keyFile))
        {
            return null;
        }
        try
        {
            var stored = JsonSerializer.Deserialize<StoredKeys>(File.ReadAllText(_keyFile));
            if (stored is null || string.IsNullOrEmpty(stored.PublicKey) || string.IsNullOrEmpty(stored.PrivateKeyEnc))
            {
                return null;
            }
            var publicKey = Base64Url.Decode(stored.PublicKey);
            var privateKey = Base64Url.Decode(_protector.Unprotect(stored.PrivateKeyEnc));
            return new VapidKeys(publicKey, privateKey);
        }
        catch
        {
            return null; // corrupt/undecryptable → regenerate (invalidates old subs, better than a dead channel)
        }
    }

    private VapidKeys Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        LeftPad(p.Q.X!, 32).CopyTo(publicKey, 1);
        LeftPad(p.Q.Y!, 32).CopyTo(publicKey, 33);

        var keys = new VapidKeys(publicKey, LeftPad(p.D!, 32));
        Persist(keys);
        return keys;
    }

    private void Persist(VapidKeys keys)
    {
        var stored = new StoredKeys(
            keys.PublicKeyBase64Url,
            _protector.Protect(Base64Url.Encode(keys.PrivateKey)));
        Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
        var tmp = _keyFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored));
        File.Move(tmp, _keyFile, overwrite: true);
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

    private sealed record StoredKeys(string PublicKey, string PrivateKeyEnc);
}
