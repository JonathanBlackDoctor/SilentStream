using System.Text;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public class TitleTemplaterTests
{
    [Fact]
    public void Expands_date_tokens_in_the_default_template()
    {
        var result = TitleTemplater.Expand(
            "라이브 - {yyyy-MM-dd HH:mm}", new DateTime(2026, 6, 12, 9, 30, 0));

        Assert.Equal("라이브 - 2026-06-12 09:30", result);
    }

    [Fact]
    public void Plain_text_templates_pass_through()
    {
        Assert.Equal("고정 제목", TitleTemplater.Expand("고정 제목", DateTime.Now));
    }

    [Fact]
    public void Multiple_tokens_are_all_expanded()
    {
        var result = TitleTemplater.Expand(
            "{yyyy}년 {MM}월 방송", new DateTime(2026, 6, 12));

        Assert.Equal("2026년 06월 방송", result);
    }

    [Fact]
    public void Room_token_is_substituted_when_name_present()
    {
        var result = TitleTemplater.Expand(
            "[{호실}] 라이브 - {yyyy-MM-dd HH:mm}", new DateTime(2026, 6, 23, 9, 30, 0), "201호");

        Assert.Equal("[201호] 라이브 - 2026-06-23 09:30", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_room_collapses_bracketed_prefix(string? roomName)
    {
        // No 호실명: the "[{호실}] " prefix must vanish entirely (not render "[] 라이브").
        var result = TitleTemplater.Expand(
            "[{호실}] 라이브 - {yyyy-MM-dd}", new DateTime(2026, 6, 23), roomName);

        Assert.Equal("라이브 - 2026-06-23", result);
    }

    [Fact]
    public void Bare_room_token_with_empty_name_leaves_no_leading_space()
    {
        var result = TitleTemplater.Expand("{호실} 라이브", new DateTime(2026, 6, 23), "");

        Assert.Equal("라이브", result);
    }

    [Fact]
    public void Braces_in_room_name_cannot_inject_a_token()
    {
        // A name containing "{...}" must not smuggle a date/period token into the second pass.
        var result = TitleTemplater.Expand("[{호실}] 라이브", new DateTime(2026, 6, 23), "2{yyyy}호");

        Assert.Equal("[2yyyy호] 라이브", result);
    }
}

public class EncryptedTokenDataStoreTests : IDisposable
{
    /// <summary>Reversible fake so the store logic is testable off-Windows (no DPAPI).</summary>
    private sealed class Base64Protector : ITokenProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string ciphertext) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed record FakeToken(string RefreshToken, string AccessToken);

    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-yt-").FullName;

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"));

    [Fact]
    public async Task Token_roundtrips_through_encrypted_config_field()
    {
        var store = new EncryptedTokenDataStore(Store, new Base64Protector());
        var token = new FakeToken("1//refresh", "ya29.access");

        await store.StoreAsync("user", token);
        var loaded = await store.GetAsync<FakeToken>("user");

        Assert.Equal(token, loaded);
        // The persisted value must not contain the raw token.
        var configJson = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.DoesNotContain("1//refresh", configJson);
    }

    [Fact]
    public async Task Missing_token_returns_default()
    {
        var store = new EncryptedTokenDataStore(Store, new Base64Protector());
        Assert.Null(await store.GetAsync<FakeToken>("user"));
    }

    [Fact]
    public async Task Delete_clears_the_stored_blob()
    {
        var store = new EncryptedTokenDataStore(Store, new Base64Protector());
        await store.StoreAsync("user", new FakeToken("r", "a"));

        await store.DeleteAsync<FakeToken>("user");

        Assert.Null(await store.GetAsync<FakeToken>("user"));
        Assert.Equal(string.Empty, Store.Load().YouTube.RefreshTokenEnc);
    }

    [Fact]
    public async Task Undecryptable_blob_falls_back_to_fresh_login()
    {
        var configStore = Store;
        var config = configStore.Load();
        config.YouTube.RefreshTokenEnc = "not-base64!!!";
        configStore.Save(config);

        var store = new EncryptedTokenDataStore(configStore, new Base64Protector());

        Assert.Null(await store.GetAsync<FakeToken>("user"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
