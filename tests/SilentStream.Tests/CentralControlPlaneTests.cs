using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SilentStream.Inspection;
using Xunit;

namespace SilentStream.Tests;

public sealed class CentralControlPlaneTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silentstream-control-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Google_identity_is_limited_to_admin_or_assigned_rooms()
    {
        var options = Options.Create(new ControlPlaneOptions
        {
            AdminEmails = ["owner@example.com"],
            RoomManagers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["m112"] = ["manager@example.com"]
            }
        });
        var authorizer = new ControlPlaneAuthorizer(options);

        Assert.Equal(ControlPlaneRole.Admin, authorizer.RoleFor(User("OWNER@example.com"), "m111"));
        Assert.Equal(ControlPlaneRole.Manager, authorizer.RoleFor(User("manager@example.com"), "M112"));
        Assert.Equal(ControlPlaneRole.None, authorizer.RoleFor(User("manager@example.com"), "m113"));
        Assert.Equal(["m112"], authorizer.RoomsFor(User("manager@example.com"), ["m111", "m112", "m113"]));
    }

    [Fact]
    public void Enrollment_is_one_time_and_only_a_hash_is_persisted()
    {
        var package = Path.Combine(_root, "prepared.zip");
        File.WriteAllBytes(package, [1, 2, 3, 4, 5]);
        var options = Options.Create(new ControlPlaneOptions
        {
            StateFile = "state.json",
            PublicBaseUri = "https://admin.silentstream.win",
            PreparedPackagePath = package
        });
        var store = new ControlPlaneStore(options, new TestEnvironment(_root));
        var code = store.CreateEnrollment("M112", "manager@example.com", TimeSpan.FromMinutes(5));
        var request = new CentralEnrollmentRedeemRequest("m112", "installation-1", "M112-PC", code);

        var enrollment = store.Redeem(request, options.Value);

        Assert.NotNull(enrollment);
        Assert.Equal("m112", enrollment.RoomId);
        Assert.Equal("installation-1", enrollment.InstallationId);
        Assert.StartsWith("wss://agent.silentstream.win/", enrollment.DeviceWebSocketUri);
        Assert.Equal(5, enrollment.PackageLength);
        Assert.NotNull(store.Authenticate("m112", enrollment.DeviceId, enrollment.DeviceSecret));
        Assert.Null(store.Redeem(request, options.Value));
        var persisted = File.ReadAllText(Path.Combine(_root, "state.json"));
        Assert.DoesNotContain(code, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(enrollment.DeviceSecret, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void Destructive_commands_require_an_admin()
    {
        Assert.True(CentralCommandPolicy.TryResolve("live.start", out var start));
        Assert.Equal(ControlPlaneRole.Manager, start.MinimumRole);
        Assert.True(CentralCommandPolicy.TryResolve("system.shutdown", out var shutdown));
        Assert.Equal(ControlPlaneRole.Admin, shutdown.MinimumRole);
        foreach (var action in new[] { "system.shutdown.schedule", "system.shutdown.schedule.cancel" })
        {
            Assert.True(CentralCommandPolicy.TryResolve(action, out var scheduled));
            Assert.Equal(ControlPlaneRole.Admin, scheduled.MinimumRole);
        }
        Assert.False(CentralCommandPolicy.TryResolve("shell.execute", out _));
    }

    [Fact]
    public void Replacement_does_not_revoke_working_device_until_new_agent_connects()
    {
        var package = Path.Combine(_root, "replacement.zip");
        File.WriteAllBytes(package, [1]);
        var options = Options.Create(new ControlPlaneOptions
        {
            StateFile = "replacement-state.json",
            PublicBaseUri = "https://admin.silentstream.win",
            DeviceBaseUri = "https://agent.silentstream.win",
            PreparedPackagePath = package
        });
        var store = new ControlPlaneStore(options, new TestEnvironment(_root));
        var firstCode = store.CreateEnrollment("m112", "manager@example.com", TimeSpan.FromMinutes(5));
        var first = store.Redeem(new("m112", "installation-1", "OLD-PC", firstCode), options.Value)!;
        var firstIdentity = store.Authenticate("m112", first.DeviceId, first.DeviceSecret)!;
        store.MarkConnected(firstIdentity, true);

        var replacementCode = store.CreateEnrollment("m112", "manager@example.com", TimeSpan.FromMinutes(5));
        var replacement = store.Redeem(new("m112", "installation-2", "NEW-PC", replacementCode), options.Value)!;
        Assert.NotNull(store.Authenticate("m112", first.DeviceId, first.DeviceSecret));
        Assert.Equal("installation-1", store.GetDevice("m112", TimeSpan.FromMinutes(1))!.InstallationId);

        var replacementIdentity = store.Authenticate("m112", replacement.DeviceId, replacement.DeviceSecret)!;
        store.MarkConnected(replacementIdentity, true);
        Assert.Null(store.Authenticate("m112", first.DeviceId, first.DeviceSecret));
        Assert.Equal("installation-2", store.GetDevice("m112", TimeSpan.FromMinutes(1))!.InstallationId);
    }

    private static ClaimsPrincipal User(string email)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        identity.AddClaim(new Claim(ClaimTypes.Name, email));
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
