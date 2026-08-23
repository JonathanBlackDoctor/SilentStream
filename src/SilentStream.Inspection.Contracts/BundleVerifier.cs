using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SilentStream.Inspection;

public sealed class InspectionBundleVerifier
{
    public const string ManifestName = "inspection-manifest.json";
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxBundleBytes = 256L * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, string> _publicKeys;

    public InspectionBundleVerifier(IReadOnlyDictionary<string, string> publicKeys) => _publicKeys = publicKeys;

    public VerifiedInspectionBundle VerifyAndExtract(string zipPath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = ValidateEntries(archive);
        if (!entries.TryGetValue(ManifestName, out var manifestEntry))
        {
            throw new InvalidDataException($"{ManifestName} is missing.");
        }

        InspectionBundleManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<InspectionBundleManifest>(stream, InspectionProtocol.Json)
                ?? throw new InvalidDataException("Manifest is empty.");
        }

        ValidateManifest(manifest, entries);
        VerifySignature(manifest);

        var root = Path.GetFullPath(Path.Combine(destinationRoot, $"{Sanitize(manifest.BundleId)}-{Sanitize(manifest.Version)}"));
        var allowedRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bundle destination escaped its root.");
        }

        Directory.CreateDirectory(root);
        foreach (var pair in manifest.Files)
        {
            var entry = entries[pair.Key];
            var output = Path.GetFullPath(Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe output path: {pair.Key}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = entry.Open();
            using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(file);
        }
        File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(manifest, InspectionProtocol.Json));
        return new VerifiedInspectionBundle(manifest, root);
    }

    public static byte[] SignaturePayload(InspectionBundleManifest manifest)
    {
        var unsigned = manifest with { Signature = null };
        return JsonSerializer.SerializeToUtf8Bytes(unsigned, InspectionProtocol.Json);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.EndsWith('/')) continue;
            if (Path.IsPathRooted(name) || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".."))
            {
                throw new InvalidDataException($"Unsafe ZIP entry: {name}");
            }
            if (!result.TryAdd(name, entry)) throw new InvalidDataException($"Duplicate ZIP entry: {name}");
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000) throw new InvalidDataException($"Symbolic-link ZIP entry is not allowed: {name}");
            if (entry.Length < 0 || entry.Length > MaxEntryBytes || (total += entry.Length) > MaxBundleBytes)
            {
                throw new InvalidDataException("Bundle exceeds extraction limits.");
            }
        }
        return result;
    }

    private static void ValidateManifest(InspectionBundleManifest manifest, Dictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.SchemaVersion != InspectionProtocol.SchemaVersion) throw new InvalidDataException("Unsupported schema version.");
        if (string.IsNullOrWhiteSpace(manifest.BundleId) || string.IsNullOrWhiteSpace(manifest.SigningKeyId)) throw new InvalidDataException("Bundle identity is missing.");
        if (!Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Bundle version must be a numeric version.");
        if (!Version.TryParse(manifest.MinAgentVersion, out var minimum) || minimum > Version.Parse(InspectionProtocol.AgentVersion))
            throw new InvalidDataException("Bundle requires a newer inspection agent.");
        if (manifest.TimeoutSeconds is < 1 or > 86400) throw new InvalidDataException("Bundle timeout is outside the safe range.");
        if (manifest.Plan.Steps.Count is 0 or > 500) throw new InvalidDataException("Plan must contain 1-500 steps.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in manifest.Plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id)) throw new InvalidDataException("Step ids must be non-empty and unique.");
            if (step.TimeoutSeconds is < 1 or > 7200 || step.Retry is < 0 or > 5) throw new InvalidDataException($"Unsafe limits on step {step.Id}.");
        }
        foreach (var step in manifest.Plan.Steps)
        {
            if (step.DependsOn.Any(id => !ids.Contains(id) || id == step.Id)) throw new InvalidDataException($"Invalid dependency on step {step.Id}.");
            if (step.Compensate is { Length: > 0 } compensation && (!ids.Contains(compensation) || compensation == step.Id)) throw new InvalidDataException($"Invalid compensation on step {step.Id}.");
        }
        EnsureAcyclic(manifest.Plan.Steps);

        var payloadEntries = entries.Keys.Where(k => !string.Equals(k, ManifestName, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!payloadEntries.SetEquals(manifest.Files.Keys)) throw new InvalidDataException("Manifest file list does not exactly match ZIP payload.");
        foreach (var pair in manifest.Files)
        {
            using var stream = entries[pair.Key].Open();
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(pair.Value.ToLowerInvariant())))
                throw new InvalidDataException($"SHA-256 mismatch: {pair.Key}");
        }
    }

    private void VerifySignature(InspectionBundleManifest manifest)
    {
        if (!_publicKeys.TryGetValue(manifest.SigningKeyId, out var encoded) || string.IsNullOrWhiteSpace(manifest.Signature))
            throw new CryptographicException("Unknown signing key or missing signature.");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encoded), out _);
        if (!ecdsa.VerifyData(SignaturePayload(manifest), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256))
            throw new CryptographicException("Bundle signature is invalid.");
    }

    private static void EnsureAcyclic(IReadOnlyList<InspectionStep> steps)
    {
        var byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("Plan contains a dependency cycle.");
            foreach (var dependency in byId[id].DependsOn) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var step in steps) Visit(step.Id);
    }

    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

public sealed record VerifiedInspectionBundle(InspectionBundleManifest Manifest, string ExtractedPath);
