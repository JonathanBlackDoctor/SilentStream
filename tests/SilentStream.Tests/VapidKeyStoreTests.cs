using System.Text;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

public class VapidKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-vapidkey-").FullName;
    private readonly string _file;
    private readonly Base64Protector _protector = new();

    public VapidKeyStoreTests() => _file = Path.Combine(_dir, "vapid.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Generates_a_65_byte_public_and_32_byte_private_key()
    {
        var keys = new VapidKeyStore(_file, _protector).GetOrCreate();

        Assert.Equal(65, keys.PublicKey.Length);
        Assert.Equal(0x04, keys.PublicKey[0]);       // uncompressed point marker
        Assert.Equal(32, keys.PrivateKey.Length);
    }

    [Fact]
    public void Same_instance_returns_the_same_keypair()
    {
        var store = new VapidKeyStore(_file, _protector);

        Assert.Equal(store.GetOrCreate().PublicKeyBase64Url, store.GetOrCreate().PublicKeyBase64Url);
    }

    [Fact]
    public void Persisted_keypair_is_reused_by_a_new_instance()
    {
        var first = new VapidKeyStore(_file, _protector).GetOrCreate();

        var second = new VapidKeyStore(_file, _protector).GetOrCreate();

        Assert.Equal(first.PublicKeyBase64Url, second.PublicKeyBase64Url);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
    }

    [Fact]
    public void Private_key_is_protected_at_rest_not_stored_in_the_clear()
    {
        var keys = new VapidKeyStore(_file, _protector).GetOrCreate();

        var onDisk = File.ReadAllText(_file);
        // The raw private scalar must never appear verbatim; only its protected form is persisted.
        Assert.DoesNotContain(Base64Url.Encode(keys.PrivateKey), onDisk);
    }

    private sealed class Base64Protector : ITokenProtector
    {
        // Reversible stand-in for DPAPI, but distinct from the plaintext so "at rest" assertions hold.
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext["enc:".Length..]));
    }
}
