using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SilentStream.Core.Recovery;

/// <summary>
/// Server-side store for opaque recovery envelopes and short-lived remote-removal commands.
/// Snapshot ciphertext is never decrypted on this server.
/// </summary>
public sealed class RecoveryVaultStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly RoomRegistry _rooms;
    private readonly ProvisioningOptions _options;
    private readonly string _directory;
    private readonly Dictionary<string, ChallengeState> _challenges = new(StringComparer.Ordinal);

    public RecoveryVaultStore(RoomRegistry rooms, IOptions<ProvisioningOptions> options, IHostEnvironment environment)
    {
        _rooms = rooms;
        _options = options.Value;
        _directory = Path.IsPathRooted(_options.RecoveryDirectory)
            ? _options.RecoveryDirectory
            : Path.Combine(environment.ContentRootPath, _options.RecoveryDirectory);
    }

    public bool Register(RecoveryRegistrationRequest request)
    {
        if (!IsValidIdentity(request.Identity)) return false;
        lock (_gate)
        {
            if (!_rooms.RegisterRecoveryIdentity(request.RoomId, request.InstallationId, request.TunnelToken,
                    request.Identity.KeyId, request.Identity.ProtectionLevel)) return false;
            var identities = LoadIdentitiesLocked();
            var existing = identities.Identities.SingleOrDefault(i =>
                string.Equals(i.KeyId, request.Identity.KeyId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return string.Equals(existing.PublicKeySpki, request.Identity.PublicKeySpki, StringComparison.Ordinal);
            }
            identities.Identities.Add(request.Identity);
            SaveIdentitiesLocked(identities);
            return true;
        }
    }

    public bool StoreSnapshot(RecoverySnapshotUploadRequest request)
    {
        if (!IsValidSnapshot(request.Snapshot)) return false;
        if (!_rooms.IsRecoveryIdentityForDevice(request.RoomId, request.InstallationId, request.TunnelToken, request.Snapshot.KeyId))
            return false;

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var file = SnapshotFile(request.Snapshot.KeyId, request.Snapshot.CreatedAtUtc);
            WriteAtomically(file, JsonSerializer.Serialize(request.Snapshot, Json));
            var snapshots = Directory.EnumerateFiles(_directory, request.Snapshot.KeyId + "-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (var stale in snapshots.Skip(Math.Max(1, _options.RecoverySnapshotRetainCount)))
            {
                try { File.Delete(stale); } catch (IOException) { }
            }
            return true;
        }
    }

    public RecoveryChallenge? CreateChallenge(string keyId)
    {
        lock (_gate)
        {
            if (!IsValidKeyId(keyId) || FindIdentityLocked(keyId) is null) return null;
            PruneChallenges(DateTimeOffset.UtcNow);
            var challenge = new RecoveryChallenge(Guid.NewGuid().ToString("N"), RandomNumberGenerator.GetBytes(32),
                DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(_options.RecoveryChallengeSeconds, 15, 300)));
            _challenges[challenge.ChallengeId] = new ChallengeState(keyId, challenge.Nonce, challenge.ExpiresAtUtc);
            return challenge;
        }
    }

    public RecoverySnapshot? Restore(string challengeId, byte[] signature)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || signature.Length is 0 or > 8192) return null;
        ChallengeState? challenge;
        lock (_gate)
        {
            PruneChallenges(DateTimeOffset.UtcNow);
            if (!_challenges.Remove(challengeId, out challenge) || challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                return null;
        }

        lock (_gate)
        {
            var identity = FindIdentityLocked(challenge.KeyId);
            if (identity is null || !VerifySignature(identity.PublicKeySpki, challenge.Nonce, signature)) return null;
            var file = Directory.Exists(_directory)
                ? Directory.EnumerateFiles(_directory, challenge.KeyId + "-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (file is null) return null;
            try { return JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(file), Json); }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }
    }

    public RemovalCommandResult IssueRemoval(string roomId)
    {
        var room = _rooms.GetClaimedRoom(roomId);
        if (room is null) return new RemovalCommandResult(RemovalCommandResultKind.UnknownRoom);
        var recoveryKeyId = _rooms.GetRecoveryKeyId(room.Id);
        if (string.IsNullOrWhiteSpace(recoveryKeyId))
            return new RemovalCommandResult(RemovalCommandResultKind.RecoveryBackupUnavailable);
        lock (_gate)
        {
            var latestSnapshot = LatestSnapshotLocked(recoveryKeyId);
            if (latestSnapshot is null || !IsValidSnapshot(latestSnapshot))
                return new RemovalCommandResult(RemovalCommandResultKind.RecoveryBackupUnavailable);
            var document = LoadCommandsLocked();
            PruneCommands(document, DateTimeOffset.UtcNow);
            var command = new RemovalCommand
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                RoomId = room.Id,
                InstallationId = room.ClaimedInstallationId!,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                State = "issued"
            };
            document.Commands.Add(command);
            SaveCommandsLocked(document);
            return new RemovalCommandResult(RemovalCommandResultKind.Success, command.Id, command.ExpiresAtUtc);
        }
    }

    public RemovalConsumeResult ConsumeRemoval(string commandId, string roomId, string installationId, string tunnelToken)
    {
        if (!_rooms.VerifyInstalledDevice(roomId, installationId, tunnelToken) || !IsCommandId(commandId))
            return new RemovalConsumeResult(RemovalConsumeResultKind.Unauthorized);
        lock (_gate)
        {
            var document = LoadCommandsLocked();
            PruneCommands(document, DateTimeOffset.UtcNow);
            var command = document.Commands.SingleOrDefault(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));
            if (command is null) return new RemovalConsumeResult(RemovalConsumeResultKind.NotFound);
            if (!string.Equals(command.RoomId, roomId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(command.InstallationId, installationId, StringComparison.Ordinal) ||
                command.ExpiresAtUtc <= DateTimeOffset.UtcNow || command.State != "issued")
                return new RemovalConsumeResult(RemovalConsumeResultKind.Unauthorized);

            command.State = "consumed";
            command.ConsumedAtUtc = DateTimeOffset.UtcNow;
            command.CompletionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            SaveCommandsLocked(document);
            return new RemovalConsumeResult(RemovalConsumeResultKind.Success, command.CompletionToken);
        }
    }

    public bool CompleteRemoval(string commandId, string completionToken)
    {
        if (!IsCommandId(commandId) || string.IsNullOrWhiteSpace(completionToken)) return false;
        lock (_gate)
        {
            var document = LoadCommandsLocked();
            var command = document.Commands.SingleOrDefault(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));
            if (command is null || command.State != "consumed" || !FixedTimeEquals(command.CompletionToken, completionToken)) return false;
            command.State = "completed";
            command.CompletedAtUtc = DateTimeOffset.UtcNow;
            command.CompletionToken = null;
            SaveCommandsLocked(document);
            return true;
        }
    }

    private bool IsValidSnapshot(RecoverySnapshot snapshot)
    {
        if (snapshot.Version != RecoveryArchive.CurrentVersion || !IsValidKeyId(snapshot.KeyId) ||
            snapshot.Ciphertext.Length > _options.RecoverySnapshotMaxBytes * 2 ||
            snapshot.WrappedDataKey.Length > 16384 || snapshot.Nonce.Length > 64 || snapshot.Tag.Length > 64 ||
            snapshot.CiphertextSha256.Length != 64 || snapshot.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            return false;
        try
        {
            var ciphertext = Convert.FromBase64String(snapshot.Ciphertext);
            var checksum = Convert.FromHexString(snapshot.CiphertextSha256);
            return ciphertext.Length <= _options.RecoverySnapshotMaxBytes &&
                Convert.FromBase64String(snapshot.WrappedDataKey).Length is > 0 and <= 8192 &&
                Convert.FromBase64String(snapshot.Nonce).Length == 12 &&
                Convert.FromBase64String(snapshot.Tag).Length == 16 &&
                checksum.Length == 32 && CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(ciphertext));
        }
        catch (FormatException) { return false; }
    }

    private static bool IsValidIdentity(RecoveryIdentity identity)
    {
        if (!IsValidKeyId(identity.KeyId) || string.IsNullOrWhiteSpace(identity.PublicKeySpki) || identity.PublicKeySpki.Length > 8192) return false;
        try
        {
            var key = Convert.FromBase64String(identity.PublicKeySpki);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant(), identity.KeyId, StringComparison.Ordinal)) return false;
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out _);
            return rsa.KeySize >= 2048;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private static bool VerifySignature(string publicKeySpki, byte[] nonce, byte[] signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            return rsa.VerifyData(nonce, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private CommandDocument LoadCommandsLocked()
    {
        var file = CommandFile;
        if (!File.Exists(file)) return new CommandDocument();
        try { return JsonSerializer.Deserialize<CommandDocument>(File.ReadAllText(file), Json) ?? new CommandDocument(); }
        catch (JsonException) { return new CommandDocument(); }
        catch (IOException) { return new CommandDocument(); }
    }

    private void SaveCommandsLocked(CommandDocument document) => WriteAtomically(CommandFile, JsonSerializer.Serialize(document, Json));
    private IdentityDocument LoadIdentitiesLocked()
    {
        if (!File.Exists(IdentityFile)) return new IdentityDocument();
        try { return JsonSerializer.Deserialize<IdentityDocument>(File.ReadAllText(IdentityFile), Json) ?? new IdentityDocument(); }
        catch (JsonException) { return new IdentityDocument(); }
        catch (IOException) { return new IdentityDocument(); }
    }

    private RecoveryIdentity? FindIdentityLocked(string keyId) =>
        LoadIdentitiesLocked().Identities.SingleOrDefault(i => string.Equals(i.KeyId, keyId, StringComparison.Ordinal));

    private void SaveIdentitiesLocked(IdentityDocument document) =>
        WriteAtomically(IdentityFile, JsonSerializer.Serialize(document, Json));

    private string CommandFile => Path.Combine(_directory, "removal-commands.json");
    private string IdentityFile => Path.Combine(_directory, "recovery-identities.json");
    private string SnapshotFile(string keyId, DateTimeOffset now) => Path.Combine(_directory, $"{keyId}-{now.UtcTicks:D20}.json");

    private RecoverySnapshot? LatestSnapshotLocked(string keyId)
    {
        if (!Directory.Exists(_directory)) return null;
        var file = Directory.EnumerateFiles(_directory, keyId + "-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) return null;
        try { return JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(file), Json); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void WriteAtomically(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, file, overwrite: true);
    }

    private static bool IsValidKeyId(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsCommandId(string? value) => value is { Length: 48 } && value.All(Uri.IsHexDigit);
    private static bool FixedTimeEquals(string? a, string b)
    {
        if (a is null) return false;
        var left = System.Text.Encoding.UTF8.GetBytes(a);
        var right = System.Text.Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void PruneChallenges(DateTimeOffset now)
    {
        foreach (var id in _challenges.Where(p => p.Value.ExpiresAtUtc <= now).Select(p => p.Key).ToArray()) _challenges.Remove(id);
    }

    private static void PruneCommands(CommandDocument document, DateTimeOffset now) =>
        document.Commands.RemoveAll(c => c.ExpiresAtUtc < now.AddDays(-1));

    private sealed record ChallengeState(string KeyId, byte[] Nonce, DateTimeOffset ExpiresAtUtc);
}

public sealed class CommandDocument { public List<RemovalCommand> Commands { get; set; } = []; }
public sealed class IdentityDocument { public List<RecoveryIdentity> Identities { get; set; } = []; }
public sealed class RemovalCommand
{
    public string Id { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? CompletionToken { get; set; }
}

public enum RemovalCommandResultKind { Success, UnknownRoom, RecoveryBackupUnavailable }
public sealed record RemovalCommandResult(RemovalCommandResultKind Kind, string? CommandId = null, DateTimeOffset? ExpiresAtUtc = null);
public enum RemovalConsumeResultKind { Success, NotFound, Unauthorized }
public sealed record RemovalConsumeResult(RemovalConsumeResultKind Kind, string? CompletionToken = null);
