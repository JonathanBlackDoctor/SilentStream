using Xunit;

namespace SilentStream.Tests;

public sealed class InspectionBridgeSecurityStructureTests
{
    [Fact]
    public void Playlist_lookup_requires_operator_role_and_the_local_inspection_bridge()
    {
        var repository = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App",
            "Remote", "RemoteControlServer.cs"));
        var appRoute = Slice(appSource,
            "app.MapGet(\"/api/inspection/playlists\"",
            "app.MapPost(\"/api/inspection/report\"");
        AssertOrdered(appRoute, "HasInspectionBridgeSecret(ctx)", "_playlists.Snapshot");
        var bridgePolicy = Slice(appSource,
            "private bool HasInspectionBridgeSecret",
            "private async Task<IResult> RerunCoordinatorInspectionAsync");
        Assert.Contains("IPAddress.IsLoopback", bridgePolicy, StringComparison.Ordinal);

        var coordinatorSource = File.ReadAllText(Path.Combine(repository, "src",
            "SilentStream.Inspection.Coordinator", "Program.cs"));
        var coordinatorRoute = Slice(coordinatorSource,
            "app.MapGet(\"/inspect/api/v1/playlists\"",
            "app.MapGet(\"/inspect/api/v1/tokens\"");
        AssertOrdered(coordinatorRoute,
            "HasRole(context, InspectionRole.Operator)",
            "AppApiTokenMachineEnc",
            "X-SilentStream-Inspection-Bridge");
    }

    [Fact]
    public void Portal_has_role_gating_period_navigation_and_clipboard_fallback()
    {
        var repository = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(repository, "src",
            "SilentStream.Inspection.Coordinator", "wwwroot", "inspect", "index.html"));

        Assert.Contains("me.role==='admin'||me.role==='operator'", html, StringComparison.Ordinal);
        Assert.Contains("playlistPrev", html, StringComparison.Ordinal);
        Assert.Contains("playlistNext", html, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard", html, StringComparison.Ordinal);
        Assert.Contains("document.execCommand('copy')", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Room_manager_app_receives_playlist_history_and_limits_the_default_view()
    {
        var repository = FindRepositoryRoot();
        var agent = File.ReadAllText(Path.Combine(repository, "src",
            "SilentStream.Inspection.Coordinator", "CentralControlAgentHostedService.cs"));
        Assert.Contains("/api/inspection/playlists?knownOnly=true", agent, StringComparison.Ordinal);
        Assert.Contains("inspectionBridge: true", agent, StringComparison.Ordinal);
        Assert.Contains("X-SilentStream-Inspection-Bridge", agent, StringComparison.Ordinal);

        var appRoute = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App",
            "Remote", "RemoteControlServer.cs"));
        Assert.Contains("ctx.Request.Query[\"knownOnly\"]", appRoute, StringComparison.Ordinal);
        Assert.Contains("DateOnly.MinValue, DateOnly.MaxValue", appRoute, StringComparison.Ordinal);

        var managerApp = File.ReadAllText(Path.Combine(repository, "cloudflare", "control-plane",
            "public", "room", "app.js"));
        Assert.Contains("HISTORY_PAGE_SIZE=5", managerApp, StringComparison.Ordinal);
        Assert.Contains("items.slice(start,start+HISTORY_PAGE_SIZE)", managerApp, StringComparison.Ordinal);
        Assert.Contains("다른 날짜 찾기", managerApp, StringComparison.Ordinal);
        Assert.Contains("data-playlist-older", managerApp, StringComparison.Ordinal);
        Assert.Contains("data-copy-playlist", managerApp, StringComparison.Ordinal);
        Assert.Contains("data-copy-replay", managerApp, StringComparison.Ordinal);
        Assert.Contains("교시별 영상과 별도로 저장되는 전체 방송", managerApp, StringComparison.Ordinal);
        Assert.Contains("endedAtUtc", managerApp, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText", managerApp, StringComparison.Ordinal);

        var centralPortal = File.ReadAllText(Path.Combine(repository, "cloudflare", "control-plane",
            "public", "control", "app.js"));
        Assert.DoesNotContain("playlistPanel", centralPortal, StringComparison.Ordinal);
        Assert.DoesNotContain("data-copy-playlist", centralPortal, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_timetable_and_split_mutation_routes_are_absent()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repository, "src", "SilentStream.App",
            "Remote", "RemoteControlServer.cs"));

        Assert.DoesNotContain("/api/schedule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/splits", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspection_report_requires_the_dedicated_bridge_secret_before_reading_or_saving_a_report()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(repository, "src", "SilentStream.App", "Remote", "RemoteControlServer.cs");
        var source = File.ReadAllText(path);
        var route = Slice(source,
            "app.MapPost(\"/api/inspection/report\"",
            "app.MapPost(\"/api/inspection/command\"");

        AssertOrdered(route,
            "HasInspectionBridgeSecret(ctx)",
            "StatusCodes.Status403Forbidden",
            "ReadJsonAsync<InspectionReportRequest>(ctx)",
            "_inspection.SaveReport(report)");
        Assert.DoesNotContain("Headers.Authorization", route, StringComparison.Ordinal);

        var policy = Slice(source,
            "private bool HasInspectionBridgeSecret",
            "private async Task<IResult> RerunCoordinatorInspectionAsync");
        Assert.Contains("InspectionBridgeTokenEnc", policy, StringComparison.Ordinal);
        Assert.Contains("X-SilentStream-Inspection-Bridge", policy, StringComparison.Ordinal);
        Assert.Contains("_tokenProtector.Unprotect(encrypted)", policy, StringComparison.Ordinal);
        Assert.Contains("RemoteAuth.ConstantTimeEquals", policy, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Security marker was not found: {marker}");
            Assert.True(current > previous, $"Security marker is out of order: {marker}");
            previous = current;
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Source marker was not found after start: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }
}
