using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class RemoteCorsTests
{
    [Theory]
    [InlineData("https://201.example.com", "/api/summary", true)]
    [InlineData("http://192.168.0.5:8787", "/api/status", true)]
    [InlineData("https://201.example.com", "/api", true)]
    [InlineData("https://201.example.com", "/API/summary", true)] // path casing is not security-relevant here
    public void Cross_origin_api_requests_get_cors_headers(string origin, string path, bool expected)
    {
        Assert.Equal(expected, RemoteCors.AppliesTo(origin, path));
    }

    [Theory]
    [InlineData(null, "/api/summary")] // same-origin fetch: no Origin header
    [InlineData("", "/api/summary")]
    [InlineData("https://201.example.com", "/")]              // the HTML page is not a CORS target
    [InlineData("https://201.example.com", "/ws/status")]     // websockets are not CORS targets
    [InlineData("https://201.example.com", "/apifoo")]        // segment-safe: not the API surface
    public void Non_api_or_same_origin_requests_get_no_cors_headers(string? origin, string path)
    {
        Assert.False(RemoteCors.AppliesTo(origin, path));
    }

    [Theory]
    [InlineData("OPTIONS", "https://201.example.com", "/api/summary", true)]
    [InlineData("options", "https://201.example.com", "/api/pair", true)] // method casing tolerated
    [InlineData("GET", "https://201.example.com", "/api/summary", false)] // actual request, not preflight
    [InlineData("OPTIONS", null, "/api/summary", false)]                  // no Origin → not a CORS preflight
    [InlineData("OPTIONS", "https://201.example.com", "/", false)]        // outside the API surface
    public void Preflight_detected_only_for_cross_origin_options_on_api(
        string method, string? origin, string path, bool expected)
    {
        Assert.Equal(expected, RemoteCors.IsPreflight(method, origin, path));
    }

    [Fact]
    public void Allowed_headers_cover_every_auth_transport_the_phone_uses()
    {
        // The phone sends the token as Authorization (grid/summary fetches) or X-Device-Token,
        // plus Content-Type for JSON bodies. A missing entry would fail every preflight.
        Assert.Contains("Authorization", RemoteCors.AllowHeaders);
        Assert.Contains("Content-Type", RemoteCors.AllowHeaders);
        Assert.Contains("X-Device-Token", RemoteCors.AllowHeaders);
    }

    [Fact]
    public void Allowed_methods_cover_the_remote_api_surface()
    {
        // PUT (schedule save) and DELETE (schedule override clear) ride on preflights too.
        foreach (var method in new[] { "GET", "POST", "PUT", "DELETE", "OPTIONS" })
        {
            Assert.Contains(method, RemoteCors.AllowMethods);
        }
    }
}
