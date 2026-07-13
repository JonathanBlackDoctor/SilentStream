using System.Text;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public sealed class EncryptedFileTokenDataStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-caption-token-").FullName;

    [Fact]
    public async Task Stores_caption_oauth_data_encrypted_in_its_own_file()
    {
        var path = Path.Combine(_directory, "caption-token.dat");
        var store = new EncryptedFileTokenDataStore(path, new Base64Protector());
        var token = new FakeToken("caption-refresh", "caption-access");

        await store.StoreAsync("caption-user", token);

        Assert.True(File.Exists(path));
        Assert.DoesNotContain("caption-refresh", File.ReadAllText(path));
        Assert.Equal(token, await store.GetAsync<FakeToken>("caption-user"));
    }

    [Fact]
    public async Task Delete_removes_only_the_caption_token_file()
    {
        var path = Path.Combine(_directory, "caption-token.dat");
        var store = new EncryptedFileTokenDataStore(path, new Base64Protector());
        await store.StoreAsync("caption-user", new FakeToken("r", "a"));

        await store.DeleteAsync<FakeToken>("caption-user");

        Assert.False(File.Exists(path));
        Assert.Null(await store.GetAsync<FakeToken>("caption-user"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Base64Protector : ITokenProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed record FakeToken(string RefreshToken, string AccessToken);
}
