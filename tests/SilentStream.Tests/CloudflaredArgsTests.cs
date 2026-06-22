using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class CloudflaredArgsTests
{
    [Fact]
    public void Quick_tunnel_injects_protocol_before_url()
    {
        var args = CloudflaredArgs.Build(token: null, localPort: 8787, protocol: "http2");

        // The fix: --protocol must be present (bound to the tunnel command, before --url) so a
        // UDP-blocked network falls onto TCP/443 instead of failing the QUIC dial (2nd field test).
        Assert.Equal("tunnel --no-autoupdate --protocol http2 --url http://localhost:8787", args);
    }

    [Fact]
    public void Named_tunnel_injects_protocol_before_run()
    {
        var args = CloudflaredArgs.Build(token: "TOK123", localPort: 8787, protocol: "http2");

        Assert.Equal("tunnel --no-autoupdate --protocol http2 run --token TOK123", args);
    }

    [Theory]
    [InlineData("quic")]
    [InlineData("auto")]
    public void Protocol_value_is_passed_through(string protocol)
    {
        var args = CloudflaredArgs.Build(token: null, localPort: 9000, protocol: protocol);

        Assert.Contains($"--protocol {protocol} ", args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_protocol_omits_the_flag(string? protocol)
    {
        var quick = CloudflaredArgs.Build(token: null, localPort: 8787, protocol: protocol);
        var named = CloudflaredArgs.Build(token: "TOK", localPort: 8787, protocol: protocol);

        Assert.DoesNotContain("--protocol", quick);
        Assert.DoesNotContain("--protocol", named);
        Assert.Equal("tunnel --no-autoupdate --url http://localhost:8787", quick);
        Assert.Equal("tunnel --no-autoupdate run --token TOK", named);
    }
}
