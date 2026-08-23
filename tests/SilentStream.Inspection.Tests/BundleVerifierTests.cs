using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Inspection;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class BundleVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SilentStreamInspectionTests", Guid.NewGuid().ToString("N"));
    public BundleVerifierTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ValidSignedBundleExtracts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [new InspectionStep { Id = "wait", Type = InspectionStepType.Wait, Input = JsonSerializer.SerializeToElement(new { seconds = 1 }) }]);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var result = verifier.VerifyAndExtract(zip, Path.Combine(_root, "out"));
        Assert.Equal("bundle-test", result.Manifest.BundleId);
        Assert.True(File.Exists(Path.Combine(result.ExtractedPath, "payload.txt")));
    }

    [Fact]
    public void PayloadTamperingIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [new InspectionStep { Id = "wait", Type = InspectionStepType.Wait, Input = JsonSerializer.SerializeToElement(new { seconds = 1 }) }]);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("payload.txt")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("payload.txt").Open()); writer.Write("tampered");
        }
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void DependencyCycleIsRejectedEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var steps = new[]
        {
            new InspectionStep { Id = "a", Type = InspectionStepType.Wait, DependsOn = ["b"], Input = JsonSerializer.SerializeToElement(new { seconds = 1 }) },
            new InspectionStep { Id = "b", Type = InspectionStepType.Wait, DependsOn = ["a"], Input = JsonSerializer.SerializeToElement(new { seconds = 1 }) }
        };
        var zip = CreateBundle(key, steps);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void TokenComparisonAcceptsOnlyMatchingToken()
    {
        var token = InspectionToken.Create(); var hash = InspectionToken.Hash(token);
        Assert.True(InspectionToken.Matches(token, hash));
        Assert.False(InspectionToken.Matches(token + "x", hash));
        Assert.False(InspectionToken.Matches(token, "not-hex"));
    }

    [Fact]
    public void WrongSigningKeyIsRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(signer, [WaitStep("wait")]);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<CryptographicException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void MalformedSigningKeyEncodingIsRejectedAsCryptographicInput()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(signer, [WaitStep("wait")]);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = "not-base64" });
        Assert.Throws<CryptographicException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void PathTraversalEntryIsRejectedBeforeExtraction()
    {
        var zip = Path.Combine(_root, "traversal.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("../outside.txt");
        Assert.Throws<InvalidDataException>(() => new InspectionBundleVerifier(new Dictionary<string, string>()).VerifyAndExtract(zip, Path.Combine(_root, "out")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void DuplicateZipEntryIsRejected()
    {
        var zip = Path.Combine(_root, "duplicate.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("same.txt"); archive.CreateEntry("same.txt"); }
        Assert.Throws<InvalidDataException>(() => new InspectionBundleVerifier(new Dictionary<string, string>()).VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void CaseCollidingZipEntriesAreRejectedForWindowsExtraction()
    {
        var zip = Path.Combine(_root, "case-collision.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("payload.txt"); archive.CreateEntry("PAYLOAD.txt"); }
        Assert.Throws<InvalidDataException>(() => new InspectionBundleVerifier(new Dictionary<string, string>()).VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void UnsafeBundleIdentifierIsRejectedEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [WaitStep("wait")], bundleId: "../collision");
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void PreviouslyExtractedBundleTamperingIsDetected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [WaitStep("wait")]);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var verified = verifier.VerifyAndExtract(zip, Path.Combine(_root, "out"));
        File.WriteAllText(Path.Combine(verified.ExtractedPath, "payload.txt"), "changed");
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void NewerAgentRequirementIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [WaitStep("wait")], minimumAgent: "99.0.0");
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void MissingCompensationStepIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateBundle(key, [WaitStep("wait") with { Compensate = "missing" }]);
        var verifier = new InspectionBundleVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "out")));
    }

    [Fact]
    public void RepositoryFullPlanTemplateIsValidAndSignable()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "SilentStream.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var source = Path.Combine(repo!.FullName, "inspection", "bundles", "full-v0.4.0");
        var plan = JsonSerializer.Deserialize<InspectionPlan>(File.ReadAllText(Path.Combine(source, "plan.json")), InspectionProtocol.Json)!;
        Assert.True(plan.Steps.Count >= 60);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(source, path).Replace('\\', '/'),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), StringComparer.Ordinal);
        var manifest = new InspectionBundleManifest
        {
            BundleId="full-template-test",Version="0.4.0",MinAgentVersion="0.4.0",SigningKeyId="test",
            Files=new SortedDictionary<string,string>(files,StringComparer.Ordinal),Plan=plan,TimeoutSeconds=86400
        };
        manifest=manifest with{Signature=Convert.ToBase64String(key.SignData(InspectionBundleVerifier.SignaturePayload(manifest),HashAlgorithmName.SHA256))};
        var zip=Path.Combine(_root,"full.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))
        {
            foreach(var pair in files){var entry=archive.CreateEntry(pair.Key);using var input=File.OpenRead(Path.Combine(source,pair.Key.Replace('/',Path.DirectorySeparatorChar)));using var output=entry.Open();input.CopyTo(output);}
            using var stream=archive.CreateEntry(InspectionBundleVerifier.ManifestName).Open();JsonSerializer.Serialize(stream,manifest,InspectionProtocol.Json);
        }
        var verifier=new InspectionBundleVerifier(new Dictionary<string,string>{{"test",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}});
        var verified=verifier.VerifyAndExtract(zip,Path.Combine(_root,"full-out"));
        Assert.Equal(plan.Steps.Count,verified.Manifest.Plan.Steps.Count);
    }

    [Fact]
    public void SignedAgentUpdateIsVerifiedAndExtracted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateAgentUpdate(key);
        var verifier = new InspectionAgentUpdateVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var verified = verifier.VerifyAndExtract(zip, Path.Combine(_root, "agent-out"));
        Assert.Equal("9.1.0", verified.Manifest.Version);
        Assert.True(File.Exists(Path.Combine(verified.ExtractedPath, "broker", "SilentStreamInspectionBroker.exe")));
    }

    [Fact]
    public void TamperedAgentUpdateIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateAgentUpdate(key);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("session/SilentStreamInspectionSessionAgent.exe")!.Delete();
            using var stream = archive.CreateEntry("session/SilentStreamInspectionSessionAgent.exe").Open(); stream.Write("tampered"u8);
        }
        var verifier = new InspectionAgentUpdateVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<CryptographicException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "agent-out")));
    }

    [Fact]
    public void AgentUpdateRejectsUnsignedExtraFile()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateAgentUpdate(key);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.CreateEntry("extra.exe");
        var verifier = new InspectionAgentUpdateVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "agent-out")));
    }

    [Fact]
    public void AgentUpdateRejectsCaseCollidingEntries()
    {
        var zip = Path.Combine(_root, "agent-case-collision.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("broker/file.exe"); archive.CreateEntry("BROKER/file.exe"); }
        Assert.Throws<InvalidDataException>(() => new InspectionAgentUpdateVerifier(new Dictionary<string, string>()).VerifyAndExtract(zip, Path.Combine(_root, "agent-out")));
    }

    [Fact]
    public void AgentUpdateRejectsMalformedVersionAsInvalidData()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = CreateAgentUpdate(key, "not-a-version");
        var verifier = new InspectionAgentUpdateVerifier(new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndExtract(zip, Path.Combine(_root, "agent-out")));
    }

    private static InspectionStep WaitStep(string id) => new() { Id = id, Type = InspectionStepType.Wait, Input = JsonSerializer.SerializeToElement(new { seconds = 1 }) };

    private string CreateBundle(ECDsa key, IReadOnlyCollection<InspectionStep> steps, string minimumAgent = "0.4.0", string bundleId = "bundle-test")
    {
        var payload = Encoding.UTF8.GetBytes("original");
        var manifest = new InspectionBundleManifest
        {
            BundleId = bundleId, Version = "1.0.0", MinAgentVersion = minimumAgent, SigningKeyId = "test",
            Files = new SortedDictionary<string, string> { ["payload.txt"] = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() },
            Plan = new InspectionPlan { Name = "test", Steps = steps.ToList() }
        };
        manifest = manifest with { Signature = Convert.ToBase64String(key.SignData(InspectionBundleVerifier.SignaturePayload(manifest), HashAlgorithmName.SHA256)) };
        var zip = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using (var stream = archive.CreateEntry("payload.txt").Open()) stream.Write(payload);
        using (var stream = archive.CreateEntry(InspectionBundleVerifier.ManifestName).Open()) JsonSerializer.Serialize(stream, manifest, InspectionProtocol.Json);
        return zip;
    }

    private string CreateAgentUpdate(ECDsa key, string version = "9.1.0")
    {
        var payloads = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["broker/SilentStreamInspectionBroker.exe"] = "broker"u8.ToArray(),
            ["coordinator/SilentStreamInspectionCoordinator.exe"] = "coordinator"u8.ToArray(),
            ["session/SilentStreamInspectionSessionAgent.exe"] = "session"u8.ToArray()
        };
        var manifest = new InspectionAgentUpdateManifest
        {
            Version = version, SigningKeyId = "test",
            Files = new SortedDictionary<string, string>(payloads.ToDictionary(pair => pair.Key,
                pair => Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()), StringComparer.Ordinal)
        };
        manifest = manifest with { Signature = Convert.ToBase64String(key.SignData(InspectionAgentUpdateVerifier.SignaturePayload(manifest), HashAlgorithmName.SHA256)) };
        var zip = Path.Combine(_root, Guid.NewGuid() + "-agent.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var pair in payloads) { using var stream = archive.CreateEntry(pair.Key).Open(); stream.Write(pair.Value); }
        using (var stream = archive.CreateEntry(InspectionAgentUpdateVerifier.ManifestName).Open()) JsonSerializer.Serialize(stream, manifest, InspectionProtocol.Json);
        return zip;
    }
}
