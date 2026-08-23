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

    public BrokerConfiguration()
    {
        Root = Environment.GetEnvironmentVariable("SILENTSTREAM_INSPECTION_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaCaptureHelper", "Inspection");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "coordinator.json")));
        var root = document.RootElement;
        Secret = Unprotect(root.GetProperty("brokerSecretMachineEnc").GetString()!);
        AppExecutablePath = Get(root, "appExecutablePath");
        CachedInstallerPath = Get(root, "cachedInstallerPath");
        CachedInstallerSha256 = Get(root, "cachedInstallerSha256").ToLowerInvariant();
        PublicKeys = root.TryGetProperty("publicKeys", out var keys)
            ? keys.Deserialize<Dictionary<string, string>>() ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();
        InstallRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
            ?? throw new InvalidDataException("Broker install root is unavailable.");
    }

    private static string Get(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string Unprotect(string encoded) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(encoded), Entropy, DataProtectionScope.LocalMachine));
}
