using Xunit;

namespace SilentStream.Tests;

public sealed class RemoteRecoveryStructureTests
{
    [Fact]
    public void Installed_coordinator_does_not_disable_the_app_owned_tunnel_replica()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "SilentStream.App", "Remote", "RemoteControlServer.cs"));
        var start = source.IndexOf("private async Task StartCloudflareTunnelAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private string? ResolveCloudflareToken", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var method = source[start..end];
        Assert.Contains("앱 복구용 Cloudflare 커넥터", method, StringComparison.Ordinal);
        Assert.Contains("_cloudflared", method, StringComparison.Ordinal);
        Assert.DoesNotContain("앱 내부 터널은 시작하지 않습니다", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Phone_header_has_a_narrow_viewport_wrap_rule()
    {
        var html = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "SilentStream.App", "Remote", "index.html"));

        Assert.Contains("@media (max-width:480px)", html, StringComparison.Ordinal);
        Assert.Contains(".app-top { align-items:flex-start; flex-wrap:wrap", html, StringComparison.Ordinal);
        Assert.Contains(".quick-lock { width:100%; justify-content:flex-start; flex-wrap:wrap; white-space:normal", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_preserves_cloudflared_connection_and_error_logs()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "SilentStream.Inspection.Coordinator", "CloudflaredHostedService.cs"));

        Assert.Contains("Registered tunnel connection", source, StringComparison.Ordinal);
        Assert.Contains("cloudflared exited unexpectedly", source, StringComparison.Ordinal);
        Assert.Contains("_logger.LogWarning(\"cloudflared: {Message}\"", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }
}
