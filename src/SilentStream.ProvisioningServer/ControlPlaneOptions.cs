using System.Security.Claims;

public sealed class ControlPlaneOptions
{
    public bool Enabled { get; set; } = true;
    public string StateFile { get; set; } = "data/control-plane-state.json";
    public string PublicBaseUri { get; set; } = "https://admin.silentstream.win";
    public string DeviceBaseUri { get; set; } = "https://agent.silentstream.win";
    public string PreparedPackagePath { get; set; } = string.Empty;
    public string InstallerPath { get; set; } = string.Empty;
    public int DeviceOfflineSeconds { get; set; } = 45;
    public int EnrollmentLifetimeMinutes { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 20;
    public string AccessTeamDomain { get; set; } = string.Empty;
    public string AccessAudience { get; set; } = string.Empty;
    public List<string> AccessAudiences { get; set; } = [];
    public List<string> AdminEmails { get; set; } = [];
    public Dictionary<string, List<string>> RoomManagers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowDevelopmentIdentity { get; set; }
}

public enum ControlPlaneRole
{
    None = 0,
    Manager = 1,
    Admin = 2
}

public sealed class ControlPlaneAuthorizer
{
    private readonly ControlPlaneOptions _options;

    public ControlPlaneAuthorizer(Microsoft.Extensions.Options.IOptions<ControlPlaneOptions> options)
        => _options = options.Value;

    public ControlPlaneRole RoleFor(ClaimsPrincipal principal, string roomId)
    {
        var email = Email(principal);
        if (email is null) return ControlPlaneRole.None;
        if (_options.AdminEmails.Any(value => EqualEmail(value, email))) return ControlPlaneRole.Admin;
        return _options.RoomManagers.TryGetValue(RoomId.Normalize(roomId), out var managers)
            && managers.Any(value => EqualEmail(value, email))
            ? ControlPlaneRole.Manager
            : ControlPlaneRole.None;
    }

    public IReadOnlyList<string> RoomsFor(ClaimsPrincipal principal, IEnumerable<string> knownRooms)
    {
        var email = Email(principal);
        if (email is null) return [];
        if (_options.AdminEmails.Any(value => EqualEmail(value, email)))
            return knownRooms.Select(RoomId.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        return _options.RoomManagers
            .Where(pair => pair.Value.Any(value => EqualEmail(value, email)))
            .Select(pair => RoomId.Normalize(pair.Key))
            .Where(room => knownRooms.Any(known => RoomId.Normalize(known) == room))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
    }

    public static string? Email(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.Identity?.Name;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static bool EqualEmail(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left)
        && string.Equals(left.Trim(), right, StringComparison.OrdinalIgnoreCase);
}

public static class RoomId
{
    private static readonly HashSet<string> DefaultRooms = new(StringComparer.OrdinalIgnoreCase)
    {
        "m111", "m112", "m113", "baek202"
    };

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant()
            .Replace("강의실", string.Empty, StringComparison.Ordinal)
            .Replace("호", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.StartsWith('백') && int.TryParse(normalized[1..], out var number))
            normalized = $"baek{number}";
        return normalized;
    }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length is > 0 and <= 64
            && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static IReadOnlyCollection<string> KnownDefaults => DefaultRooms;
}
