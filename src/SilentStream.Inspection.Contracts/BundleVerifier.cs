using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SilentStream.Inspection;

public sealed class InspectionBundleVerifier
{
    public const string ManifestName = "inspection-manifest.json";
    private const int MaxEntries = 5000;
    private const int MaxPathLength = 240;
    private const long MaxManifestBytes = 4L * 1024 * 1024;
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
        if (manifestEntry.Length > MaxManifestBytes) throw new InvalidDataException("Bundle manifest is too large.");

        InspectionBundleManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<InspectionBundleManifest>(stream, InspectionProtocol.Json)
                ?? throw new InvalidDataException("Manifest is empty.");
        }

        ValidateManifest(manifest, entries);
        VerifySignature(manifest);

        var contentHash = Convert.ToHexString(SHA256.HashData(SignaturePayload(manifest))).ToLowerInvariant();
        var normalizedVersion = Version.Parse(manifest.Version).ToString();
        var root = Path.GetFullPath(Path.Combine(destinationRoot, $"{manifest.BundleId}-{normalizedVersion}-{contentHash[..12]}"));
        var allowedRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bundle destination escaped its root.");
        }

        if (Directory.Exists(root))
        {
            ValidateExtractedFiles(root, manifest);
            return new VerifiedInspectionBundle(manifest, root);
        }

        var temp = root + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var pair in manifest.Files)
            {
                var entry = entries[pair.Key];
                var output = Path.GetFullPath(Path.Combine(temp, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unsafe output path: {pair.Key}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var input = entry.Open();
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(file);
            }
            File.WriteAllText(Path.Combine(temp, ManifestName), JsonSerializer.Serialize(manifest, InspectionProtocol.Json));
            Directory.Move(temp, root);
        }
        catch
        {
            try { Directory.Delete(temp, true); } catch { }
            throw;
        }
        return new VerifiedInspectionBundle(manifest, root);
    }

    public static byte[] SignaturePayload(InspectionBundleManifest manifest)
    {
        var unsigned = manifest with { Signature = null };
        return JsonSerializer.SerializeToUtf8Bytes(unsigned, InspectionProtocol.Json);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > MaxEntries) throw new InvalidDataException("Bundle entry count is outside the allowed range.");
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var windowsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.EndsWith('/')) continue;
            var parts = name.Split('/');
            if (name.Length > MaxPathLength || Path.IsPathRooted(name) || name.StartsWith('/') || name.Contains(':')
                || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new InvalidDataException($"Unsafe ZIP entry: {name}");
            }
            if (!result.TryAdd(name, entry)) throw new InvalidDataException($"Duplicate ZIP entry: {name}");
            if (!windowsNames.Add(name)) throw new InvalidDataException($"Case-colliding ZIP entry: {name}");
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
        if (manifest.Files is null || manifest.Plan is null || !IsSafeIdentifier(manifest.BundleId, 128) || !IsSafeIdentifier(manifest.SigningKeyId, 128))
            throw new InvalidDataException("Bundle identity contains unsupported characters or is too long.");
        if (!Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Bundle version must be a numeric version.");
        if (!Version.TryParse(manifest.MinAgentVersion, out var minimum) || minimum > Version.Parse(InspectionProtocol.AgentVersion))
            throw new InvalidDataException("Bundle requires a newer inspection agent.");
        if (manifest.TimeoutSeconds is < 1 or > 86400) throw new InvalidDataException("Bundle timeout is outside the safe range.");
        if (string.IsNullOrWhiteSpace(manifest.Plan.Name) || manifest.Plan.Name.Length > 200) throw new InvalidDataException("Plan name is missing or too long.");
        if (manifest.Plan.Steps is null || manifest.Plan.Steps.Count is 0 or > 500) throw new InvalidDataException("Plan must contain 1-500 steps.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in manifest.Plan.Steps)
        {
            if (step is null || !IsSafeIdentifier(step.Id, 128) || !ids.Add(step.Id)) throw new InvalidDataException("Step ids must be safe, non-empty and unique.");
            if (step.Input.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"Input is missing on step {step.Id}.");
            if (step.TimeoutSeconds is < 1 or > 7200 || step.Retry is < 0 or > 5) throw new InvalidDataException($"Unsafe limits on step {step.Id}.");
        }
        foreach (var step in manifest.Plan.Steps)
        {
            if (step.DependsOn is null) throw new InvalidDataException($"Dependencies are missing on step {step.Id}.");
            if (step.DependsOn.Any(id => !ids.Contains(id) || id == step.Id)) throw new InvalidDataException($"Invalid dependency on step {step.Id}.");
            if (step.Compensate is { Length: > 0 } compensation && (!ids.Contains(compensation) || compensation == step.Id)) throw new InvalidDataException($"Invalid compensation on step {step.Id}.");
        }
        EnsureAcyclic(manifest.Plan.Steps);

        var payloadEntries = entries.Keys.Where(k => !string.Equals(k, ManifestName, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!payloadEntries.SetEquals(manifest.Files.Keys)) throw new InvalidDataException("Manifest file list does not exactly match ZIP payload.");
        foreach (var pair in manifest.Files)
        {
            if (pair.Value is null || pair.Value.Length != 64 || pair.Value.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException($"Invalid SHA-256 declaration: {pair.Key}");
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
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encoded), out _);
            if (!ecdsa.VerifyData(SignaturePayload(manifest), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256))
                throw new CryptographicException("Bundle signature is invalid.");
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Bundle signing key or signature encoding is invalid.", ex);
        }
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

    private static bool IsSafeIdentifier(string? value, int maxLength) => value is not null && value.Length is > 0 && value.Length <= maxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static void ValidateExtractedFiles(string root, InspectionBundleManifest manifest)
    {
        foreach (var pair in manifest.Files)
        {
            var path = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new InvalidDataException($"Previously extracted bundle file is missing: {pair.Key}");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(pair.Value.ToLowerInvariant())))
                throw new InvalidDataException($"Previously extracted bundle file changed: {pair.Key}");
        }
    }
}

public sealed record VerifiedInspectionBundle(InspectionBundleManifest Manifest, string ExtractedPath);
