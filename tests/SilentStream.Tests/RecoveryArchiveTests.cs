using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Core.Recovery;
using Xunit;

namespace SilentStream.Tests;

public sealed class RecoveryArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mch-recovery-").FullName;

    [Fact]
    public void Creates_an_encrypted_allow_list_and_restores_only_operational_state()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "period-assets", "audio"));
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "config.json"), "{\"refresh\":\"secret-token\"}");
        File.WriteAllText(Path.Combine(source, "upload_queue.json"), "[\"queued\"]");
        File.WriteAllText(Path.Combine(source, "period-assets", "assets.json"), "[]");
        File.WriteAllText(Path.Combine(source, "period-assets", "audio", "should-not-copy.m4a"), "media");
        File.WriteAllText(Path.Combine(source, "logs", "should-not-copy.log"), "log");

        var keys = new TestRecoveryKeyStore();
        var snapshot = RecoveryArchive.Create(source, keys, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("secret-token", snapshot.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("queued", snapshot.Ciphertext, StringComparison.Ordinal);

        var destination = Path.Combine(_root, "destination");
        RecoveryArchive.Restore(destination, snapshot, keys);

        Assert.Equal("{\"refresh\":\"secret-token\"}", File.ReadAllText(Path.Combine(destination, "config.json")));
        Assert.True(File.Exists(Path.Combine(destination, "upload_queue.json")));
        Assert.True(File.Exists(Path.Combine(destination, "period-assets", "assets.json")));
        Assert.False(File.Exists(Path.Combine(destination, "logs", "should-not-copy.log")));
        Assert.False(File.Exists(Path.Combine(destination, "period-assets", "audio", "should-not-copy.m4a")));
    }

    [Fact]
    public void Rejects_a_tampered_snapshot_before_writing_files()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.json"), "{\"x\":1}");
        var keys = new TestRecoveryKeyStore();
        var snapshot = RecoveryArchive.Create(source, keys, DateTimeOffset.UtcNow);
        var last = snapshot.Ciphertext[^1] == 'A' ? 'B' : 'A';
        var tampered = snapshot with { Ciphertext = snapshot.Ciphertext[..^1] + last };

        var destination = Path.Combine(_root, "destination");
        Assert.Throws<CryptographicException>(() => RecoveryArchive.Restore(destination, tampered, keys));
        Assert.False(File.Exists(Path.Combine(destination, "config.json")));

        using var otherWindowsUser = new TestRecoveryKeyStore();
        Assert.Throws<CryptographicException>(() => RecoveryArchive.Restore(destination, snapshot, otherWindowsUser));
    }

    [Fact]
    public void Restore_removes_work_that_references_media_not_in_the_archive()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "period-assets"));
        File.WriteAllText(Path.Combine(source, "config.json"), "{}");
        File.WriteAllText(Path.Combine(source, "upload_queue.json"),
            "{\"jobs\":[{\"id\":\"missing\",\"filePath\":\"C:\\\\missing.mp4\",\"status\":\"pending\"},{\"id\":\"remote\",\"filePath\":\"C:\\\\old.mp4\",\"status\":\"completed\",\"videoId\":\"youtube-id\"}]}" );
        File.WriteAllText(Path.Combine(source, "pending_splits.json"),
            "{\"splits\":[{\"id\":\"missing\",\"sessionFilePath\":\"C:\\\\missing.mp4\",\"status\":\"approved\"}],\"chain\":{\"sessionFilePath\":\"C:\\\\missing.mp4\"}}" );
        File.WriteAllText(Path.Combine(source, "period-assets", "assets.json"),
            "{\"assets\":[{\"id\":\"orphan\",\"audioPath\":\"C:\\\\missing.m4a\"},{\"id\":\"video\",\"audioPath\":\"C:\\\\missing.m4a\",\"videoId\":\"youtube-id\"}]}" );

        using var keys = new TestRecoveryKeyStore();
        var snapshot = RecoveryArchive.Create(source, keys, DateTimeOffset.UtcNow);
        var destination = Path.Combine(_root, "destination");
        RecoveryArchive.Restore(destination, snapshot, keys);

        using var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "upload_queue.json")));
        Assert.Equal("remote", queue.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString());
        using var splits = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "pending_splits.json")));
        Assert.Empty(splits.RootElement.GetProperty("splits").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, splits.RootElement.GetProperty("chain").ValueKind);
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "period-assets", "assets.json")));
        var restored = assets.RootElement.GetProperty("assets");
        Assert.Single(restored.EnumerateArray());
        Assert.Equal("video", restored[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, restored[0].GetProperty("audioPath").ValueKind);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed class TestRecoveryKeyStore : IRecoveryKeyStore, IDisposable
    {
        private readonly RSA _rsa = RSA.Create(3072);
        private readonly RecoveryIdentity _identity;

        public TestRecoveryKeyStore()
        {
            var spki = _rsa.ExportSubjectPublicKeyInfo();
            _identity = new RecoveryIdentity(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                Convert.ToBase64String(spki), "test");
        }

        public RecoveryIdentity GetOrCreate() => _identity;
        public RecoveryIdentity? TryOpen() => _identity;
        public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey) => _rsa.Encrypt(dataKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
        public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrapped) => _rsa.Decrypt(wrapped.ToArray(), RSAEncryptionPadding.OaepSHA256);
        public byte[] Sign(ReadOnlySpan<byte> data) => _rsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        public void Dispose() => _rsa.Dispose();
    }
}
