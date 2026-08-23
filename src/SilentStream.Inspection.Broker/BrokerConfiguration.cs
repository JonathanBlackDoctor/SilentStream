using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SilentStream.Inspection.Broker;

public sealed class BrokerConfiguration
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SilentStream.Inspection.v1");
    public string Root { get; }
    public string Secret { get; }
    public string AppExecutablePath { get; }
    public string CachedInstallerPath { get; }
    public string CachedInstallerSha256 { get; }
    public IReadOnlyDictionary<string, string> PublicKeys { get; }
    public string InstallRoot { get; }
    public string TrustedStagingRoot { get; }
    public string UpdateRunnerRoot { get; }

    public BrokerConfiguration()
    {
        Root = Environment.GetEnvironmentVariable("SILENTSTREAM_INSPECTION_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaCaptureHelper", "Inspection");
        InstallRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
            ?? throw new InvalidDataException("Broker install root is unavailable.");
        var productRoot = Directory.GetParent(InstallRoot)?.FullName ?? throw new InvalidDataException("Broker product root is unavailable.");
        var policyPath = Environment.GetEnvironmentVariable("SILENTSTREAM_INSPECTION_BROKER_POLICY")
            ?? Path.Combine(productRoot, "InspectionPolicy", "broker-policy.json");

        using (var coordinatorDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "coordinator.json"))))
            Secret = Unprotect(coordinatorDocument.RootElement.GetProperty("brokerSecretMachineEnc").GetString()!);
        using var policyDocument = JsonDocument.Parse(File.ReadAllText(policyPath));
        var policy = policyDocument.RootElement;
        if (policy.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported broker policy version.");
        AppExecutablePath = Get(policy, "appExecutablePath");
        CachedInstallerPath = Get(policy, "cachedInstallerPath");
        CachedInstallerSha256 = Get(policy, "cachedInstallerSha256").ToLowerInvariant();
        PublicKeys = policy.TryGetProperty("publicKeys", out var keys)
            ? keys.Deserialize<Dictionary<string, string>>() ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();
        TrustedStagingRoot = Path.Combine(productRoot, "InspectionAgentStaging");
        UpdateRunnerRoot = Path.Combine(productRoot, "InspectionUpdater");
    }

    private static string Get(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string Unprotect(string encoded) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(encoded), Entropy, DataProtectionScope.LocalMachine));
}
