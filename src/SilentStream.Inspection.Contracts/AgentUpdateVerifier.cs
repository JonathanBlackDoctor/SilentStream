using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SilentStream.Inspection;

public sealed record InspectionAgentUpdateManifest
{
    public int SchemaVersion { get; init; } = InspectionProtocol.SchemaVersion;
    public string Version { get; init; } = InspectionProtocol.AgentVersion;
    public string SigningKeyId { get; init; } = string.Empty;
    public SortedDictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    public string? Signature { get; init; }
}

public sealed record VerifiedInspectionAgentUpdate(InspectionAgentUpdateManifest Manifest, string ExtractedPath, string PackageSha256);

public sealed class InspectionAgentUpdateVerifier
{
    public const string ManifestName = "inspection-agent-update.json";
    private const long MaxFileBytes = 300L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private static readonly string[] RequiredFiles =
    [
        "coordinator/SilentStreamInspectionCoordinator.exe",
        "broker/SilentStreamInspectionBroker.exe",
        "session/SilentStreamInspectionSessionAgent.exe"
    ];
    private readonly IReadOnlyDictionary<string, string> _publicKeys;

    public InspectionAgentUpdateVerifier(IReadOnlyDictionary<string, string> publicKeys) => _publicKeys = publicKeys;

    public VerifiedInspectionAgentUpdate VerifyAndExtract(string zipPath, string destinationRoot)
    {
        var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = Normalize(entry.FullName);
            if (path.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (!entries.TryAdd(path, entry)) throw new InvalidDataException($"Duplicate update entry: {path}");
            if (entry.Length is < 0 or > MaxFileBytes || entry.CompressedLength > MaxFileBytes) throw new InvalidDataException($"Update entry is too large: {path}");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("Update expands beyond the allowed size.");
            if ((entry.ExternalAttributes & 0xF0000000) == 0xA0000000) throw new InvalidDataException("Symbolic links are not allowed in updates.");
        }
        if (!entries.TryGetValue(ManifestName, out var manifestEntry)) throw new InvalidDataException($"Missing {ManifestName}.");
        InspectionAgentUpdateManifest manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<InspectionAgentUpdateManifest>(stream, InspectionProtocol.Json) ?? throw new InvalidDataException("Update manifest is empty.");
        ValidateManifest(manifest, entries.Keys);
        if (!_publicKeys.TryGetValue(manifest.SigningKeyId, out var publicKey)) throw new CryptographicException("Update signing key is not trusted.");
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!ecdsa.VerifyData(SignaturePayload(manifest), Convert.FromBase64String(manifest.Signature!), HashAlgorithmName.SHA256))
                throw new CryptographicException("Update signature is invalid.");
        }
        foreach (var pair in manifest.Files)
        {
            using var stream = entries[pair.Key].Open();
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!FixedHex(actual, pair.Value)) throw new CryptographicException($"Update file hash mismatch: {pair.Key}");
        }
        var version = Version.Parse(manifest.Version);
        var target = Path.GetFullPath(Path.Combine(destinationRoot, $"agent-{version}-{packageHash[..12]}"));
        var root = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update extraction target escaped its root.");
        if (Directory.Exists(target)) return new VerifiedInspectionAgentUpdate(manifest, target, packageHash);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var pair in manifest.Files)
            {
                var output = Path.GetFullPath(Path.Combine(temp, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update extraction escaped its root.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var source = entries[pair.Key].Open();
                using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
            }
            File.WriteAllText(Path.Combine(temp, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true }), new UTF8Encoding(false));
            Directory.Move(temp, target);
        }
        catch { try { Directory.Delete(temp, true); } catch { } throw; }
        return new VerifiedInspectionAgentUpdate(manifest, target, packageHash);
    }

    public static byte[] SignaturePayload(InspectionAgentUpdateManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest with { Signature = null }, InspectionProtocol.Json);

    private static void ValidateManifest(InspectionAgentUpdateManifest manifest, IEnumerable<string> archivePaths)
    {
        if (manifest.SchemaVersion != InspectionProtocol.SchemaVersion) throw new InvalidDataException("Unsupported update schema version.");
        _ = Version.Parse(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.SigningKeyId) || string.IsNullOrWhiteSpace(manifest.Signature)) throw new InvalidDataException("Update signature metadata is missing.");
        if (manifest.Files.Count is < 3 or > 4000) throw new InvalidDataException("Update file count is outside the allowed range.");
        foreach (var required in RequiredFiles) if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException($"Required update component is missing: {required}");
        foreach (var pair in manifest.Files)
        {
            if (Normalize(pair.Key) != pair.Key || pair.Value.Length != 64 || pair.Value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException($"Invalid update file declaration: {pair.Key}");
        }
        var expected = manifest.Files.Keys.Append(ManifestName).Order(StringComparer.Ordinal).ToArray();
        var actual = archivePaths.Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) throw new InvalidDataException("Update ZIP contents do not exactly match the signed manifest.");
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(':') || normalized.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException($"Unsafe update path: {path}");
        return normalized;
    }

    private static bool FixedHex(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
}
