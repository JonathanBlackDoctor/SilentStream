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

    public VapidKeyInstallResult Install(VapidKeys keys)
    {
        if (!VapidKeyMaterial.IsValid(keys))
        {
            return VapidKeyInstallResult.Invalid;
        }

        lock (_gate)
        {
            var existing = _cached ??= Load();
            if (existing is not null &&
                CryptographicOperations.FixedTimeEquals(existing.PublicKey, keys.PublicKey) &&
                CryptographicOperations.FixedTimeEquals(existing.PrivateKey, keys.PrivateKey))
            {
                return VapidKeyInstallResult.Unchanged;
            }

            var replacement = new VapidKeys(keys.PublicKey.ToArray(), keys.PrivateKey.ToArray());
            Persist(replacement);
            _cached = replacement;
            return VapidKeyInstallResult.Replaced;
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
            var keys = new VapidKeys(publicKey, privateKey);
            return VapidKeyMaterial.IsValid(keys) ? keys : null;
        }
        catch
        {
            return null; // corrupt/undecryptable → regenerate (invalidates old subs, better than a dead channel)
        }
    }

    private VapidKeys Generate()
    {
        var keys = VapidKeyMaterial.Generate();
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

    private sealed record StoredKeys(string PublicKey, string PrivateKeyEnc);
}
