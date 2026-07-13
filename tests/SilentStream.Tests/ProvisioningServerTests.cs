using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SilentStream.Core.Remote.WebPush;
using Xunit;

namespace SilentStream.Tests;

public sealed class ProvisioningServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-provisioning-").FullName;
    private readonly string _roomsFile;

    public ProvisioningServerTests() => _roomsFile = Path.Combine(_dir, "rooms.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Claim_includes_the_configured_shared_vapid_key()
    {
        var shared = SharedKey();
        var registry = CreateRegistry(shared);

        var result = registry.Claim(new ClaimRequest("m111", "install-1", "M111-PC", "code"), allowRoomOnlyEnrollment: true);

        Assert.Equal(ClaimResultKind.Success, result.Kind);
        Assert.Equal(shared, result.Assignment!.SharedVapid);
    }

    [Fact]
    public void Refresh_returns_shared_key_only_to_the_claimed_installation_with_its_tunnel_proof()
    {
        var shared = SharedKey();
        var registry = CreateRegistry(shared);
        registry.Claim(new ClaimRequest("m111", "install-1", "M111-PC", "code"), allowRoomOnlyEnrollment: true);

        var success = registry.RefreshSharedVapid(new VapidRefreshRequest("m111", "install-1", "tunnel-secret"));
        var wrongInstallation = registry.RefreshSharedVapid(new VapidRefreshRequest("m111", "install-2", "tunnel-secret"));
        var wrongToken = registry.RefreshSharedVapid(new VapidRefreshRequest("m111", "install-1", "wrong-secret"));

        Assert.Equal(VapidRefreshResultKind.Success, success.Kind);
        Assert.Equal(shared, success.SharedVapid);
        Assert.Equal(VapidRefreshResultKind.Unauthorized, wrongInstallation.Kind);
        Assert.Equal(VapidRefreshResultKind.Unauthorized, wrongToken.Kind);
    }

    [Fact]
    public void Refresh_reports_not_configured_without_leaking_room_assignment()
    {
        var registry = CreateRegistry(sharedVapid: null);
        registry.Claim(new ClaimRequest("m111", "install-1", "M111-PC", "code"), allowRoomOnlyEnrollment: true);

        var result = registry.RefreshSharedVapid(new VapidRefreshRequest("m111", "install-1", "tunnel-secret"));

        Assert.Equal(VapidRefreshResultKind.NotConfigured, result.Kind);
        Assert.Null(result.SharedVapid);
    }

    private RoomRegistry CreateRegistry(SharedVapidKey? sharedVapid)
    {
        var document = new RoomDocument
        {
            SharedVapid = sharedVapid,
            Rooms =
            [
                new RoomSecret
                {
                    Id = "m111",
                    DisplayName = "M111",
                    CloudflareHostname = "m111.example.test",
                    CloudflareTunnelToken = "tunnel-secret",
                    ActivationCodeHash = "unused-in-room-only-mode"
                }
            ]
        };
        File.WriteAllText(_roomsFile, JsonSerializer.Serialize(document));
        return new RoomRegistry(Options.Create(new ProvisioningOptions { RoomsFile = _roomsFile }), new TestEnvironment(_dir));
    }

    private static SharedVapidKey SharedKey()
    {
        var keys = VapidKeyMaterial.Generate();
        return new SharedVapidKey(keys.PublicKeyBase64Url, Base64Url.Encode(keys.PrivateKey));
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ProvisioningTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
