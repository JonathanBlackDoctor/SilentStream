using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilentStream.Inspection;

public static class InspectionProtocol
{
    public const int SchemaVersion = 1;
    public static string AgentVersion { get; } = AssemblyVersion();
    public const string SessionPipeName = "SilentStream.Inspection.Session.v1";
    public const string BrokerPipeName = "SilentStream.Inspection.Broker.v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static InspectionProtocol() => Json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

    private static string AssemblyVersion()
    {
        var version = typeof(InspectionProtocol).Assembly.GetName().Version ?? new Version(0, 4, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}

public enum InspectionRole
{
    Observer = 0,
    Operator = 1,
    Admin = 2
}

public enum InspectionStepType
{
    Collect,
    AppApi,
    Wait,
    DesktopProbe,
    Script,
    Privileged,
    Fault,
    Checkpoint,
    Cleanup
}

public enum InspectionRunStatus
{
    Queued,
    Running,
    Paused,
    Pass,
    Fail,
    Unknown,
    Skipped,
    Cancelled
}

public sealed record InspectionBundleManifest
{
    public int SchemaVersion { get; init; } = InspectionProtocol.SchemaVersion;
    public string BundleId { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string MinAgentVersion { get; init; } = InspectionProtocol.AgentVersion;
    public string SigningKeyId { get; init; } = string.Empty;
    public SortedDictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    public InspectionPlan Plan { get; init; } = new();
    public string[] Capabilities { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 7200;
    public string? Signature { get; init; }
}

public sealed record InspectionPlan
{
    public string Name { get; init; } = string.Empty;
    public List<InspectionStep> Steps { get; init; } = [];
}

public sealed record InspectionStep
{
    public string Id { get; init; } = string.Empty;
    public InspectionStepType Type { get; init; }
    public string[] DependsOn { get; init; } = [];
    public bool Required { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 300;
    public int Retry { get; init; }
    public string? Compensate { get; init; }
    public JsonElement Input { get; init; }
}

public sealed record RunRequest(string BundleId, string? ConfirmationId);
public sealed record PairRequest(string Pin, string? DeviceName);
public sealed record PairResponse(string Token, InspectionRole Role);
public sealed record ControlLeaseRequest(string? Label);
public sealed record ControlLeaseResponse(string LeaseId, DateTimeOffset ExpiresAtUtc);
public sealed record DestructiveConfirmationRequest(string Action, string RunId);
public sealed record DestructiveConfirmationResponse(string ConfirmationId, DateTimeOffset ExpiresAtUtc);

public sealed record DesktopMonitor(int Index, int X, int Y, int Width, int Height, bool Primary, string Name);
public sealed record DesktopInput(
    string LeaseId,
    long FrameId,
    int Monitor,
    string Kind,
    double X,
    double Y,
    int Button,
    int Key,
    int Delta,
    bool Down);

public sealed record SessionRequest(string Id, string Action, JsonElement? Payload = null);
public sealed record SessionResponse(string Id, bool Ok, string? Error, JsonElement? Payload = null);
public sealed record BrokerRequest(string Id, string Action, JsonElement? Payload, string ConfirmationId, DateTimeOffset RequestedAtUtc, string Auth);
public sealed record BrokerResponse(string Id, bool Ok, string Message, JsonElement? Payload = null);

public static class InspectionToken
{
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string Hash(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool Matches(string token, string hash)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Hash(token)), Convert.FromHexString(hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
