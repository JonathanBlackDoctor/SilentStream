using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SilentStream.Inspection;

try
{
    if (args.Length >= 3 && args[0] == "key" && args[1] == "init")
    {
        var keyId = args[2];
        var output = args.Length >= 4 ? args[3] : $"{keyId}.public-key.json";
        CreateKey(keyId, output);
        Console.WriteLine($"Created non-exportable signing key '{keyId}'. Public key: {Path.GetFullPath(output)}");
        return 0;
    }
    if (args.Length == 8 && args[0] == "bundle" && args[1] == "sign")
    {
        SignBundle(args[2], args[3], args[4], args[5], args[6], args[7]);
        Console.WriteLine($"Signed inspection bundle: {Path.GetFullPath(args[7])}");
        return 0;
    }
    if (args.Length == 5 && args[0] == "bundle" && args[1] == "verify")
    {
        VerifyBundle(args[2], args[3], args[4]);
        return 0;
    }
    if (args.Length == 5 && args[0] == "bundle" && args[1] == "verify-startup")
    {
        VerifyStartupBundle(args[2], args[3], args[4]);
        return 0;
    }
    if (args.Length == 6 && args[0] == "agent-update" && args[1] == "sign")
    {
        SignAgentUpdate(args[2], args[3], args[4], args[5]);
        Console.WriteLine($"Signed inspection-agent update: {Path.GetFullPath(args[5])}");
        return 0;
    }
    if (args.Length == 5 && args[0] == "agent-update" && args[1] == "verify")
    {
        VerifyAgentUpdate(args[2], args[3], args[4]);
        return 0;
    }
    if (args.Length == 10 && args[0] == "deployment" && args[1] == "prepare")
    {
        PrepareDeployment(args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]);
        return 0;
    }
    Console.Error.WriteLine("Usage:\n  key init <keyId> [public-key.json]\n  bundle sign <sourceDir> <bundleId> <version> <minAgentVersion> <keyId> <output.zip>\n  bundle verify <bundle.zip> <public-key.json> <outputDir>\n  bundle verify-startup <bundle.zip> <public-key.json> <outputDir>\n  agent-update sign <payloadDir> <version> <keyId> <output.zip>\n  agent-update verify <update.zip> <public-key.json> <outputDir>\n  deployment prepare <packageDir> <bundleSourceDir> <bundleId> <version> <minAgentVersion> <keyId> <preparedPackage.zip> <operatorVault.json>");
    return 2;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

static string KeyName(string id) => $"SilentStream.Inspection.Signing.{id}";

static void CreateKey(string keyId, string output)
{
    if (string.IsNullOrWhiteSpace(keyId)) throw new ArgumentException("Key id is empty.");
    CngKey key;
    if (CngKey.Exists(KeyName(keyId))) key = CngKey.Open(KeyName(keyId));
    else
    {
        key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName(keyId), new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey
        });
    }
    using (key)
    using (var ecdsa = new ECDsaCng(key))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            keyId,
            algorithm = "ECDSA_P256_SHA256",
            subjectPublicKeyInfo = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())
        }, new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true }));
    }
}

static void SignBundle(string sourceDirectory, string bundleId, string version, string minimumAgent, string keyId, string output,
    InspectionPlan? validatedPlan = null)
{
    var source = Path.GetFullPath(sourceDirectory);
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
    var planPath = Path.Combine(source, "plan.json");
    if (!File.Exists(planPath)) throw new FileNotFoundException("sourceDir must contain plan.json.", planPath);
    var plan = validatedPlan ?? JsonSerializer.Deserialize<InspectionPlan>(File.ReadAllText(planPath), InspectionProtocol.Json)
        ?? throw new InvalidDataException("plan.json is empty.");
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        if (relative.Equals(InspectionBundleVerifier.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
        files[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
    var manifest = new InspectionBundleManifest
    {
        BundleId = bundleId,
        Version = version,
        MinAgentVersion = minimumAgent,
        SigningKeyId = keyId,
        Files = files,
        Plan = plan,
        Capabilities = plan.Steps.Select(s => s.Type.ToString()).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        TimeoutSeconds = Math.Clamp(plan.Steps.Sum(s => s.TimeoutSeconds), 1, 86400)
    };
    if (!CngKey.Exists(KeyName(keyId))) throw new CryptographicException($"Signing key '{keyId}' does not exist for this Windows user.");
    using var key = CngKey.Open(KeyName(keyId));
    using var ecdsa = new ECDsaCng(key);
    manifest = manifest with { Signature = Convert.ToBase64String(ecdsa.SignData(InspectionBundleVerifier.SignaturePayload(manifest), HashAlgorithmName.SHA256)) };
    var destination = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var temp = destination + ".tmp";
    if (File.Exists(temp)) File.Delete(temp);
    using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
    {
        foreach (var relative in files.Keys)
        {
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            using var input = File.OpenRead(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)));
            using var target = entry.Open(); input.CopyTo(target);
        }
        var manifestEntry = archive.CreateEntry(InspectionBundleVerifier.ManifestName, CompressionLevel.Optimal);
        using var stream = manifestEntry.Open(); JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true });
    }
    File.Move(temp, destination, true);
}

static void SignAgentUpdate(string payloadDirectory, string version, string keyId, string output)
{
    var source = Path.GetFullPath(payloadDirectory);
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
    var files = HashFiles(source, InspectionAgentUpdateVerifier.ManifestName);
    var manifest = new InspectionAgentUpdateManifest
    {
        Version = Version.Parse(version).ToString(),
        SigningKeyId = keyId,
        Files = files
    };
    if (!CngKey.Exists(KeyName(keyId))) throw new CryptographicException($"Signing key '{keyId}' does not exist for this Windows user.");
    using var key = CngKey.Open(KeyName(keyId));
    using var ecdsa = new ECDsaCng(key);
    manifest = manifest with { Signature = Convert.ToBase64String(ecdsa.SignData(InspectionAgentUpdateVerifier.SignaturePayload(manifest), HashAlgorithmName.SHA256)) };
    WriteSignedZip(source, files, InspectionAgentUpdateVerifier.ManifestName, manifest, output);
}

static void VerifyBundle(string bundle, string publicKeyPath, string outputDirectory)
{
    using var document = JsonDocument.Parse(File.ReadAllText(publicKeyPath));
    var keyId = document.RootElement.GetProperty("keyId").GetString() ?? throw new InvalidDataException("Public key id is missing.");
    var key = document.RootElement.GetProperty("subjectPublicKeyInfo").GetString() ?? throw new InvalidDataException("Public key data is missing.");
    var verified = new InspectionBundleVerifier(new Dictionary<string, string> { [keyId] = key })
        .VerifyAndExtract(bundle, outputDirectory);
    Console.WriteLine($"Verified bundle: {verified.Manifest.BundleId} {verified.Manifest.Version}, steps={verified.Manifest.Plan.Steps.Count}");
}

static void VerifyStartupBundle(string bundle, string publicKeyPath, string outputDirectory)
{
    using var document = JsonDocument.Parse(File.ReadAllText(publicKeyPath));
    var keyId = document.RootElement.GetProperty("keyId").GetString() ?? throw new InvalidDataException("Public key id is missing.");
    var key = document.RootElement.GetProperty("subjectPublicKeyInfo").GetString() ?? throw new InvalidDataException("Public key data is missing.");
    var verified = new InspectionBundleVerifier(new Dictionary<string, string> { [keyId] = key })
        .VerifyAndExtract(bundle, outputDirectory);
    OperatorCliStartupValidation.ValidateVerifiedBundle(verified.Manifest);
    Console.WriteLine($"Verified safe startup bundle: {verified.Manifest.BundleId} {verified.Manifest.Version}, steps={verified.Manifest.Plan.Steps.Count}");
}

static void VerifyAgentUpdate(string update, string publicKeyPath, string outputDirectory)
{
    using var document = JsonDocument.Parse(File.ReadAllText(publicKeyPath));
    var keyId = document.RootElement.GetProperty("keyId").GetString() ?? throw new InvalidDataException("Public key id is missing.");
    var key = document.RootElement.GetProperty("subjectPublicKeyInfo").GetString() ?? throw new InvalidDataException("Public key data is missing.");
    var verified = new InspectionAgentUpdateVerifier(new Dictionary<string, string> { [keyId] = key })
        .VerifyAndExtract(update, outputDirectory);
    Console.WriteLine($"Verified inspection-agent update: {verified.Manifest.Version}, sha256={verified.PackageSha256}");
}

static void PrepareDeployment(string packageDirectory, string bundleSourceDirectory, string bundleId, string version,
    string minimumAgent, string keyId, string outputPackage, string operatorVault)
{
    var validatedPlan = OperatorCliStartupValidation.ValidateDeploymentSource(bundleSourceDirectory, version);
    var package = Path.GetFullPath(packageDirectory);
    if (!Directory.Exists(package) || !File.Exists(Path.Combine(package, "원격점검_설치.exe")))
        throw new InvalidDataException("packageDir is not a built SilentStream inspection package.");
    var output = Path.GetFullPath(outputPackage);
    var vault = Path.GetFullPath(operatorVault);
    var packagePrefix = package.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (output.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) || vault.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Prepared ZIP and operator vault must be outside packageDir.");

    var publicKey = Path.Combine(package, $"{keyId}.public-key.json");
    CreateKey(keyId, publicKey);
    var bundleDirectory = Path.Combine(package, "preseed-bundles");
    Directory.CreateDirectory(bundleDirectory);
    var bundleName = $"{SafeName(bundleId)}-{Version.Parse(version)}.zip";
    var bundlePath = Path.Combine(bundleDirectory, bundleName);
    SignBundle(bundleSourceDirectory, bundleId, version, minimumAgent, keyId, bundlePath, validatedPlan);

    var adminToken = InspectionToken.Create();
    var preseed = new
    {
        schemaVersion = 1,
        signingKeyId = keyId,
        tokens = new[] { new { hash = InspectionToken.Hash(adminToken), role = "admin", label = "사전 등록 관리자" } },
        bundles = new[] { $"preseed-bundles/{bundleName}" },
        agentUpdates = Array.Empty<string>()
    };
    File.WriteAllText(Path.Combine(package, "deployment-preseed.json"), JsonSerializer.Serialize(preseed,
        new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true }), new UTF8Encoding(false));
    CreatePackageZip(package, output);
    var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
    var vaultValue = new
    {
        schemaVersion = 1,
        createdAtUtc = DateTimeOffset.UtcNow,
        keyId,
        adminToken,
        preparedPackage = output,
        preparedPackageSha256 = packageHash,
        note = "이 파일은 작업실 PC에만 보관하고 실기기로 복사하지 마세요."
    };
    Directory.CreateDirectory(Path.GetDirectoryName(vault)!);
    File.WriteAllText(vault, JsonSerializer.Serialize(vaultValue, new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true }), new UTF8Encoding(false));
    RestrictToCurrentUser(vault);
    Console.WriteLine($"Prepared zero-touch package: {output}");
    Console.WriteLine($"Operator-only vault: {vault}");
    Console.WriteLine($"Package SHA-256: {packageHash}");
}

static SortedDictionary<string, string> HashFiles(string source, string excludedManifest)
{
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        if (relative.Equals(excludedManifest, StringComparison.OrdinalIgnoreCase)) continue;
        files[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
    return files;
}

static void WriteSignedZip<T>(string source, SortedDictionary<string, string> files, string manifestName, T manifest, string output)
{
    var destination = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var temp = destination + ".tmp";
    if (File.Exists(temp)) File.Delete(temp);
    using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
    {
        foreach (var relative in files.Keys)
        {
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            using var input = File.OpenRead(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)));
            using var target = entry.Open(); input.CopyTo(target);
        }
        var manifestEntry = archive.CreateEntry(manifestName, CompressionLevel.Optimal);
        using var stream = manifestEntry.Open(); JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true });
    }
    File.Move(temp, destination, true);
}

static void CreatePackageZip(string source, string output)
{
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var temp = output + ".tmp";
    if (File.Exists(temp)) File.Delete(temp);
    using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            using var input = File.OpenRead(path); using var target = entry.Open(); input.CopyTo(target);
        }
    }
    File.Move(temp, output, true);
}

static void RestrictToCurrentUser(string path)
{
    var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
    var security = new FileSecurity();
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
}

static string SafeName(string value)
{
    var safe = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
    return safe.Length > 0 ? safe : throw new InvalidDataException("Identifier cannot be converted to a safe file name.");
}

public static class OperatorCliStartupValidation
{
    public static InspectionPlan ValidateDeploymentSource(string sourceDirectory, string requestedVersion)
    {
        EnsureCurrentVersion(requestedVersion, "Requested bundle version");
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        var planPath = Path.Combine(source, "plan.json");
        if (!File.Exists(planPath)) throw new FileNotFoundException("bundleSourceDir must contain plan.json.", planPath);
        var plan = JsonSerializer.Deserialize<InspectionPlan>(File.ReadAllText(planPath), InspectionProtocol.Json)
            ?? throw new InvalidDataException("plan.json is empty.");
        StartupBundleSafetyPolicy.EnsureSafe(plan);
        return plan;
    }

    public static void ValidateVerifiedBundle(InspectionBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        EnsureCurrentVersion(manifest.Version, "Startup bundle manifest version");
        StartupBundleSafetyPolicy.EnsureSafe(manifest.Plan);
    }

    private static void EnsureCurrentVersion(string version, string label)
    {
        if (!string.Equals(version, InspectionProtocol.AgentVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} must exactly match inspection agent version {InspectionProtocol.AgentVersion}.");
        }
    }
}
