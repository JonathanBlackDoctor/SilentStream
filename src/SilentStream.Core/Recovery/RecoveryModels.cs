using System.Security.Cryptography;

namespace SilentStream.Core.Recovery;

/// <summary>Public part of the durable, per-Windows-user recovery identity.</summary>
public sealed record RecoveryIdentity(string KeyId, string PublicKeySpki, string ProtectionLevel);

/// <summary>Abstraction over a non-exportable Windows CNG/TPM private key.</summary>
public interface IRecoveryKeyStore
{
    RecoveryIdentity GetOrCreate();
    RecoveryIdentity? TryOpen();
    byte[] WrapDataKey(ReadOnlySpan<byte> dataKey);
    byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey);
    byte[] Sign(ReadOnlySpan<byte> data);
}

/// <summary>
/// Opaque client-side encrypted backup. The server deliberately stores these fields verbatim and
/// has no private key needed to inspect the archive.
/// </summary>
public sealed record RecoverySnapshot(
    int Version,
    string KeyId,
    DateTimeOffset CreatedAtUtc,
    string WrappedDataKey,
    string Nonce,
    string Ciphertext,
    string Tag,
    string CiphertextSha256);

public sealed record RecoveryChallenge(string ChallengeId, byte[] Nonce, DateTimeOffset ExpiresAtUtc);

public sealed record RecoveryStatus(bool Ready, DateTimeOffset? LastSuccessfulBackupUtc, string Message);

public sealed record RecoveryRegistrationRequest(
    string RoomId,
    string InstallationId,
    string TunnelToken,
    RecoveryIdentity Identity);

public sealed record RecoverySnapshotUploadRequest(
    string RoomId,
    string InstallationId,
    string TunnelToken,
    RecoverySnapshot Snapshot);

public sealed record RecoveryChallengeRequest(string KeyId);

public sealed record RecoveryRestoreRequest(string ChallengeId, string Signature);

public sealed record RemoteRemovalCommand(string CommandId);

public sealed record RemoteRemovalAuthorization(string CompletionToken);
