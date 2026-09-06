using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed record InspectionTokenEntry(
    string Hash,
    InspectionRole Role,
    string Label,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    DateTimeOffset? RevokedAtUtc = null,
    DateTimeOffset? LastUsedAtUtc = null);

public sealed class CoordinatorOptions
{
    public static List<string> CreateDefaultManagedRooms() => ["m111", "m112", "m113", "baek202"];

    public int SchemaVersion { get; set; } = 1;
    public int Port { get; set; } = 8790;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string Room { get; set; } = Environment.MachineName;
    public string RoomId { get; set; } = string.Empty;
    public List<string> ManagedRooms { get; set; } = CreateDefaultManagedRooms();
    public Dictionary<string, List<string>> RequiredMicrophoneDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool StartupInspectionEnabled { get; set; } = true;
    public int StartupInspectionDelaySeconds { get; set; } = 20;
    public string PairingPin { get; set; } = CreatePin();
    public List<InspectionTokenEntry> Tokens { get; set; } = [];
    public Dictionary<string, string> PublicKeys { get; set; } = new(StringComparer.Ordinal);
    public string CloudflareTunnelTokenMachineEnc { get; set; } = string.Empty;
    public string CloudflareHostname { get; set; } = string.Empty;
    public string CloudflaredPath { get; set; } = string.Empty;
    public string CloudflareProtocol { get; set; } = "http2";
    public bool CentralControlEnabled { get; set; }
    public string CentralControlBaseUri { get; set; } = string.Empty;
    public string CentralControlWebSocketUri { get; set; } = string.Empty;
    public string CentralDeviceId { get; set; } = string.Empty;
    public string CentralInstallationId { get; set; } = string.Empty;
    public string CentralDeviceSecretMachineEnc { get; set; } = string.Empty;
    public string BrokerSecretMachineEnc { get; set; } = string.Empty;
    public string AppApiTokenMachineEnc { get; set; } = string.Empty;
    public string AppExecutablePath { get; set; } = string.Empty;
    public string AppConfigPath { get; set; } = string.Empty;
    public string CachedInstallerPath { get; set; } = string.Empty;
    public string CachedInstallerSha256 { get; set; } = string.Empty;

    public static string CreatePin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}

public sealed class CoordinatorOptionsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SilentStream.Inspection.v1");
    private readonly object _gate = new();
    public string Root { get; }
    public string ConfigPath => Path.Combine(Root, "coordinator.json");
    public string DatabasePath => Path.Combine(Root, "inspection.db");
    public string BundlesPath => Path.Combine(Root, "bundles");
    public string ArtifactsPath => Path.Combine(Root, "artifacts");
    public string PreseedBundlesPath => Path.Combine(Root, "preseed-bundles");
    public string IncomingAgentUpdatesPath => Path.Combine(Root, "incoming-agent-updates");
    public string StagedAgentUpdatesPath => Path.Combine(Root, "agent-updates");
    public string UpdateResultsPath => Path.Combine(Root, "update-results");
    public CoordinatorOptions Current { get; private set; }

    public CoordinatorOptionsStore(string? rootOverride = null)
    {
        Root = rootOverride
            ?? Environment.GetEnvironmentVariable("SILENTSTREAM_INSPECTION_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaCaptureHelper", "Inspection");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BundlesPath);
        Directory.CreateDirectory(ArtifactsPath);
        Directory.CreateDirectory(PreseedBundlesPath);
        Directory.CreateDirectory(IncomingAgentUpdatesPath);
        Directory.CreateDirectory(StagedAgentUpdatesPath);
        Directory.CreateDirectory(UpdateResultsPath);
        Current = Load();
        EnsureBrokerSecret();
    }

    public void Update(Action<CoordinatorOptions> update)
    {
        lock (_gate)
        {
            update(Current);
            Save();
        }
    }

    public bool RevokeToken(string tokenId)
    {
        var changed = false;
        Update(options =>
        {
            var index = options.Tokens.FindIndex(t => t.Hash.StartsWith(tokenId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            options.Tokens[index] = options.Tokens[index] with { RevokedAtUtc = DateTimeOffset.UtcNow };
            changed = true;
        });
        return changed;
    }

    public void MarkTokenUsed(string hash)
    {
        Update(options =>
        {
            var index = options.Tokens.FindIndex(t => t.Hash == hash);
            if (index >= 0) options.Tokens[index] = options.Tokens[index] with { LastUsedAtUtc = DateTimeOffset.UtcNow };
        });
    }

    public string UnprotectMachine(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }

    public static string ProtectMachine(string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(bytes);
    }

    private CoordinatorOptions Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var created = new CoordinatorOptions();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(created, InspectionProtocol.Json));
            return created;
        }
        var loaded = JsonSerializer.Deserialize<CoordinatorOptions>(File.ReadAllText(ConfigPath), InspectionProtocol.Json)
            ?? throw new InvalidDataException("Coordinator configuration is empty.");
        loaded.ManagedRooms ??= CoordinatorOptions.CreateDefaultManagedRooms();
        loaded.RequiredMicrophoneDeviceIds ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        loaded.StartupInspectionDelaySeconds = Math.Clamp(loaded.StartupInspectionDelaySeconds, 0, 300);
        if (loaded.CentralControlEnabled && string.IsNullOrWhiteSpace(loaded.CentralControlWebSocketUri))
            loaded.CentralControlEnabled = false;
        return loaded;
    }

    private void EnsureBrokerSecret()
    {
        if (!string.IsNullOrWhiteSpace(Current.BrokerSecretMachineEnc)) return;
        Update(o => o.BrokerSecretMachineEnc = ProtectMachine(InspectionToken.Create()));
    }

    private void Save()
    {
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, InspectionProtocol.Json));
        File.Move(temp, ConfigPath, true);
    }
}
