using System.Net;
using System.Text;
using SilentStream.Core.Provisioning;
using Xunit;

namespace SilentStream.Tests;

public sealed class RoomProvisioningClientTests
{
    [Fact]
    public async Task Catalog_returns_only_safe_room_labels_and_never_exposes_a_token()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("""
            { "requiresActivationCode": false, "rooms": [
              { "id": "m112", "name": "M112" },
              { "id": "bad id", "name": "skip" },
              { "id": "m111", "name": "M111", "cloudflareTunnelToken": "must-not-bind" }
            ] }
            """)));
        using var client = new RoomProvisioningClient(new Uri("https://provision.example/"), http);

        var catalog = await client.GetCatalogAsync();

        Assert.False(catalog.RequiresActivationCode);
        Assert.Equal(["m111", "m112"], catalog.Rooms.Select(r => r.Id));
    }

    [Fact]
    public async Task Claim_posts_room_and_device_then_returns_only_that_assignment()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Json("""
                { "roomId": "m111", "displayName": "M111", "cloudflareHostname": "m111.silentstream.win",
                  "cloudflareTunnelToken": "single-room-token", "port": 8787, "cloudflareProtocol": "http2" }
                """);
        }));
        using var client = new RoomProvisioningClient(new Uri("https://provision.example/base/"), http);

        var assignment = await client.ClaimAsync(new ProvisioningClaimRequest("m111", "install-1", "M111-PC", null));

        Assert.Equal("m111", assignment.RoomId);
        Assert.Equal("M111", assignment.DisplayName);
        Assert.Equal("m111.silentstream.win", assignment.CloudflareHostname);
        Assert.Equal("single-room-token", assignment.CloudflareTunnelToken);
        Assert.Equal(HttpMethod.Post, captured?.Method);
        Assert.Equal("/base/api/claims", captured?.RequestUri?.AbsolutePath);
    }

    [Theory]
    [InlineData("http://provision.example")]
    [InlineData("ftp://provision.example")]
    [InlineData("https://provision.example/path?unsafe=true")]
    public void Public_non_https_service_urls_are_rejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new RoomProvisioningClient(url));
    }

    [Fact]
    public async Task Claim_surfaces_server_error_without_exposing_response_body()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{ "error": "이 호실은 이미 다른 PC에 등록되어 있습니다." }""", Encoding.UTF8, "application/json")
        }));
        using var client = new RoomProvisioningClient(new Uri("https://provision.example/"), http);

        var ex = await Assert.ThrowsAsync<RoomProvisioningException>(() =>
            client.ClaimAsync(new ProvisioningClaimRequest("m111", "install-1", "M111-PC", null)));

        Assert.Equal("이 호실은 이미 다른 PC에 등록되어 있습니다.", ex.Message);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
