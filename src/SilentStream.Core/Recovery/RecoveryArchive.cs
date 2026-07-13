using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SilentStream.Core.Recovery;

/// <summary>
/// Builds a deliberately small allow-list archive of operational state. Media, logs, arbitrary
/// user paths, and any files not named here can never be uploaded by this feature.
/// </summary>
public static class RecoveryArchive
{
    public const int CurrentVersion = 1;

    private static readonly string[] AllowedFiles =
    [
        "config.json",
        "client_secret.json",
        "youtube_caption_token.dat",
        "upload_queue.json",
        "pending_splits.json",
        "push_subscriptions.json",
        "vapid_keys.json",
        ".consent",
        "period-assets/assets.json"
    ];

    public static IReadOnlyList<string> AllowedRelativePaths => AllowedFiles;

    public static RecoverySnapshot Create(string appDataDirectory, IRecoveryKeyStore keys, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(keys);

        var plainArchive = CreateZip(appDataDirectory);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plainArchive.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(dataKey, tagSizeInBytes: tag.Length);
            aes.Encrypt(nonce, plainArchive, ciphertext, tag, AssociatedData(CurrentVersion, keys.GetOrCreate().KeyId));
            var wrapped = keys.WrapDataKey(dataKey);
            return new RecoverySnapshot(
                CurrentVersion,
                keys.GetOrCreate().KeyId,
                now,
                Convert.ToBase64String(wrapped),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag),
                Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plainArchive);
        }
    }

    /// <summary>
    /// Validates all cryptographic and path boundaries before atomically replacing only the
    /// allow-listed files. Call before configuration is loaded on a reinstall.
    /// </summary>
    public static void Restore(string appDataDirectory, RecoverySnapshot snapshot, IRecoveryKeyStore keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(keys);
        if (snapshot.Version != CurrentVersion || !string.Equals(snapshot.KeyId, keys.TryOpen()?.KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException("Recovery snapshot does not belong to this Windows identity.");
        }

        var ciphertext = Convert.FromBase64String(snapshot.Ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(snapshot.CiphertextSha256), SHA256.HashData(ciphertext)))
        {
            throw new CryptographicException("Recovery snapshot checksum mismatch.");
        }

        var wrapped = Convert.FromBase64String(snapshot.WrappedDataKey);
        var nonce = Convert.FromBase64String(snapshot.Nonce);
        var tag = Convert.FromBase64String(snapshot.Tag);
        var dataKey = keys.UnwrapDataKey(wrapped);
        var zip = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dataKey, tagSizeInBytes: tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, zip, AssociatedData(snapshot.Version, snapshot.KeyId));
            RestoreZip(appDataDirectory, zip);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(zip);
        }
    }

    private static byte[] CreateZip(string appDataDirectory)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var relative in AllowedFiles)
            {
                var fullPath = SafePath(appDataDirectory, relative);
                if (!File.Exists(fullPath))
                {
                    continue;
                }
                var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
                using var destination = entry.Open();
                using var source = File.OpenRead(fullPath);
                source.CopyTo(destination);
            }
        }
        return output.ToArray();
    }

    private static void RestoreZip(string appDataDirectory, byte[] zip)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(appDataDirectory))
            ?? throw new InvalidOperationException("AppData directory has no parent.");
        var staging = Path.Combine(parent, ".MediaCaptureHelper-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var input = new MemoryStream(zip, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            foreach (var entry in archive.Entries)
            {
                if (!AllowedFiles.Contains(entry.FullName, StringComparer.Ordinal) || string.IsNullOrEmpty(entry.Name))
                {
                    throw new InvalidDataException("Recovery archive contains a disallowed path.");
                }
                var destination = SafePath(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open();
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }

            foreach (var relative in AllowedFiles)
            {
                var source = SafePath(staging, relative);
                if (!File.Exists(source))
                {
                    continue;
                }
                var destination = SafePath(appDataDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: true);
            }

            // Original recordings and exported audio are deliberately not part of a recovery
            // archive. Keep recovered operational metadata only when it still points at media
            // that remained in the user-selected recording directory; otherwise prune the stale
            // work before any worker can try to process it.
            ReconcileMediaReferences(appDataDirectory);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] AssociatedData(int version, string keyId) =>
        Encoding.UTF8.GetBytes($"MediaCaptureHelper.Recovery|{version}|{keyId}");

    private static void ReconcileMediaReferences(string appDataDirectory)
    {
        ReconcileJson(Path.Combine(appDataDirectory, "upload_queue.json"), root =>
        {
            if (root["jobs"] is not JsonArray jobs) return false;
            var changed = false;
            for (var index = jobs.Count - 1; index >= 0; index--)
            {
                if (jobs[index] is not JsonObject job) continue;
                var status = StringValue(job["status"]);
                if (status is not ("pending" or "uploading")) continue;
                if (File.Exists(StringValue(job["filePath"]))) continue;
                jobs.RemoveAt(index);
                changed = true;
            }
            return changed;
        });

        ReconcileJson(Path.Combine(appDataDirectory, "pending_splits.json"), root =>
        {
            var changed = false;
            if (root["splits"] is JsonArray splits)
            {
                for (var index = splits.Count - 1; index >= 0; index--)
                {
                    if (splits[index] is not JsonObject split) continue;
                    var status = StringValue(split["status"]);
                    if (status is not ("pending" or "approved")) continue;
                    if (File.Exists(StringValue(split["sessionFilePath"]))) continue;
                    splits.RemoveAt(index);
                    changed = true;
                }
            }
            if (root["chain"] is JsonObject chain && !File.Exists(StringValue(chain["sessionFilePath"])))
            {
                root["chain"] = null;
                changed = true;
            }
            return changed;
        });

        ReconcileJson(Path.Combine(appDataDirectory, "period-assets", "assets.json"), root =>
        {
            if (root["assets"] is not JsonArray assets) return false;
            var changed = false;
            for (var index = assets.Count - 1; index >= 0; index--)
            {
                if (assets[index] is not JsonObject asset) continue;
                var audioPath = StringValue(asset["audioPath"]);
                if (!string.IsNullOrWhiteSpace(audioPath) && !File.Exists(audioPath))
                {
                    asset["audioPath"] = null;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(StringValue(asset["audioPath"])) &&
                    string.IsNullOrWhiteSpace(StringValue(asset["videoId"])))
                {
                    assets.RemoveAt(index);
                    changed = true;
                }
            }
            return changed;
        });
    }

    private static void ReconcileJson(string path, Func<JsonObject, bool> reconcile)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || !reconcile(root)) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (JsonException)
        {
            // Existing store loaders preserve malformed state for inspection. Recovery must not
            // make an otherwise verified archive unusable merely because optional queue data is bad.
        }
        catch (IOException)
        {
            // A best-effort cleanup must never roll back the verified, atomically restored state.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: configuration restoration remains valid if optional cleanup is blocked.
        }
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string SafePath(string root, string relative)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!result.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Recovery path escapes its root.");
        }
        return result;
    }
}
