using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SilentStream.Core.Recovery;
using Xunit;

namespace SilentStream.Tests;

public sealed class RecoveryVaultStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mch-vault-").FullName;

    [Fact]
    public void Stores_opaque_snapshot_restores_after_signature_and_consumes_removal_once()
    {
        using var keys = new RecoveryArchiveTests.TestRecoveryKeyStore();
        var registry = CreateRegistry();
        var options = Options.Create(new ProvisioningOptions
        {
            RoomsFile = Path.Combine(_root, "rooms.json"),
            RecoveryDirectory = Path.Combine(_root, "vault"),
            RecoverySnapshotRetainCount = 3
        });
        var vault = new RecoveryVaultStore(registry, options, new TestEnvironment(_root));

        Assert.Equal(RemovalCommandResultKind.RecoveryBackupUnavailable, vault.IssueRemoval("m111").Kind);
        Assert.True(vault.Register(new RecoveryRegistrationRequest("m111", "install-1", "tunnel-token", keys.GetOrCreate())));
        Assert.DoesNotContain(keys.GetOrCreate().PublicKeySpki, File.ReadAllText(Path.Combine(_root, "rooms.json")), StringComparison.Ordinal);
        var source = Path.Combine(_root, "state");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.json"), "{\"secret\":\"never-on-server\"}");
        var snapshot = RecoveryArchive.Create(source, keys, DateTimeOffset.UtcNow);
        Assert.False(vault.StoreSnapshot(new RecoverySnapshotUploadRequest("m111", "install-1", "tunnel-token",
            snapshot with { CiphertextSha256 = new string('0', 64) })));
        Assert.True(vault.StoreSnapshot(new RecoverySnapshotUploadRequest("m111", "install-1", "tunnel-token", snapshot)));
        for (var index = 1; index <= 3; index++)
        {
            Assert.True(vault.StoreSnapshot(new RecoverySnapshotUploadRequest("m111", "install-1", "tunnel-token",
                snapshot with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-index) })));
        }
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(_root, "vault"), snapshot.KeyId + "-*.json").Count());

        var stored = Directory.EnumerateFiles(Path.Combine(_root, "vault"), snapshot.KeyId + "-*.json")
            .Select(File.ReadAllText).ToArray();
        Assert.All(stored, text => Assert.DoesNotContain("never-on-server", text, StringComparison.Ordinal));

        var challenge = vault.CreateChallenge(snapshot.KeyId);
        Assert.NotNull(challenge);
        var restored = vault.Restore(challenge!.ChallengeId, keys.Sign(challenge.Nonce));
        Assert.Equal(snapshot.Ciphertext, restored?.Ciphertext);
        Assert.Null(vault.Restore(challenge.ChallengeId, keys.Sign(challenge.Nonce)));

        var command = vault.IssueRemoval("m111");
        Assert.Equal(RemovalCommandResultKind.Success, command.Kind);
        Assert.Equal(RemovalConsumeResultKind.Unauthorized,
            vault.ConsumeRemoval(command.CommandId!, "m222", "install-2", "tunnel-token-2").Kind);
        var consumed = vault.ConsumeRemoval(command.CommandId!, "m111", "install-1", "tunnel-token");
        Assert.Equal(RemovalConsumeResultKind.Success, consumed.Kind);
        Assert.Equal(RemovalConsumeResultKind.Unauthorized,
            vault.ConsumeRemoval(command.CommandId!, "m111", "install-1", "tunnel-token").Kind);
        Assert.True(vault.CompleteRemoval(command.CommandId!, consumed.CompletionToken!));
        Assert.False(vault.CompleteRemoval(command.CommandId!, consumed.CompletionToken!));

        var expired = vault.IssueRemoval("m111");
        File.WriteAllText(Path.Combine(_root, "vault", "removal-commands.json"), JsonSerializer.Serialize(new
        {
            commands = new[]
            {
                new { id = expired.CommandId, roomId = "m111", installationId = "install-1",
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), state = "issued" }
            }
        }));
        Assert.Equal(RemovalConsumeResultKind.Unauthorized,
            vault.ConsumeRemoval(expired.CommandId!, "m111", "install-1", "tunnel-token").Kind);
    }

    [Fact]
    public void Rejects_snapshot_from_a_different_installation()
    {
        using var keys = new RecoveryArchiveTests.TestRecoveryKeyStore();
        var registry = CreateRegistry();
        var vault = new RecoveryVaultStore(registry, Options.Create(new ProvisioningOptions
        {
            RoomsFile = Path.Combine(_root, "rooms.json"), RecoveryDirectory = Path.Combine(_root, "vault")
        }), new TestEnvironment(_root));
        Assert.True(vault.Register(new RecoveryRegistrationRequest("m111", "install-1", "tunnel-token", keys.GetOrCreate())));
        var source = Path.Combine(_root, "state");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.json"), "{}");
        var snapshot = RecoveryArchive.Create(source, keys, DateTimeOffset.UtcNow);

        Assert.False(vault.StoreSnapshot(new RecoverySnapshotUploadRequest("m111", "install-other", "tunnel-token", snapshot)));
    }

    private RoomRegistry CreateRegistry()
    {
        var file = Path.Combine(_root, "rooms.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new RoomDocument
        {
            Rooms =
            [
                new RoomSecret
                {
                    Id = "m111", DisplayName = "M111", CloudflareHostname = "m111.example.test",
                    CloudflareTunnelToken = "tunnel-token", ActivationCodeHash = "ignored",
                    ClaimedInstallationId = "install-1", ClaimedMachineName = "M111-PC", ClaimedAtUtc = DateTimeOffset.UtcNow
                },
                new RoomSecret
                {
                    Id = "m222", DisplayName = "M222", CloudflareHostname = "m222.example.test",
                    CloudflareTunnelToken = "tunnel-token-2", ActivationCodeHash = "ignored",
                    ClaimedInstallationId = "install-2", ClaimedMachineName = "M222-PC", ClaimedAtUtc = DateTimeOffset.UtcNow
                }
            ]
        }));
        return new RoomRegistry(Options.Create(new ProvisioningOptions { RoomsFile = file }), new TestEnvironment(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
