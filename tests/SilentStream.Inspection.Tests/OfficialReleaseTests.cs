using System.Net;
using System.Text.Json;
using SilentStream.Inspection.Installer;
using Xunit;

namespace SilentStream.Inspection.Tests;
public class OfficialReleaseTests
{
    [Fact]
    public async Task Matching_immutable_release_returns_bounded_verified_asset()
    {
        using var client = Client();
        var asset = await OfficialRelease.FindAsync(client, "0.4.11", false, requestFactory: (method, uri) => new HttpRequestMessage(method, uri));
        Assert.Equal(123, asset.Length);
        Assert.Equal(new string('a', 64), asset.Sha256);
    }
    [Theory]
    [InlineData("0x4x11")]
    [InlineData("../0.4.11")]
    [InlineData("0.4.11/bad")]
    public async Task Version_cannot_inject_paths(string version)
    {
        using var client = Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialRelease.FindAsync(client, version, false));
    }
    [Theory]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("tag")]
    [InlineData("duplicate")]
    public async Task Invalid_release_is_rejected(string mutation)
    {
        using var client = Client(mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialRelease.FindAsync(client, "0.4.11", false, requestFactory: (method, uri) => new HttpRequestMessage(method, uri)));
    }
    private static HttpClient Client(string mutation = "") => new(new Handler(mutation));
    private sealed class Handler(string mutation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://agent.silentstream.win/api/updates/v0.4.11/manifest.json", request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            var asset = new {
                name = "SilentStream-Central-Installer-v0.4.11.exe",
                url = mutation == "url" ? "https://other.invalid/update.exe"
                    : "https://agent.silentstream.win/api/updates/SilentStream-Central-Installer-v0.4.11.exe",
                sha256 = mutation == "hash" ? "bad" : new string('a',64),
                length = mutation == "size" ? 2L * 1024 * 1024 * 1024 : 123
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new {version = mutation == "tag" ? "0.4.10" : "0.4.11",
                    draft = mutation == "draft", prerelease = false,
                    assets = mutation == "duplicate" ? new[] {asset,asset} : new[] {asset}})) });
        }
    }
}
