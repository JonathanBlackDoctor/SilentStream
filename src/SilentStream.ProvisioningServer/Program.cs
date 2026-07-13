using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Operational helper: generates the PBKDF2 value stored in data/rooms.json without ever writing
// the plaintext enrollment code to disk. Example:
// dotnet run --project src/SilentStream.ProvisioningServer -- hash-code "long-random-code"
if (args is ["hash-code", var code])
{
    Console.WriteLine(ActivationCodeHasher.Hash(code));
    return;
}

builder.Services.Configure<ProvisioningOptions>(builder.Configuration.GetSection("Provisioning"));
builder.Services.AddSingleton<RoomRegistry>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Lists only opaque id + display label. Tunnel hostnames and tokens are never returned here.
app.MapGet("/api/rooms", (RoomRegistry rooms, IOptions<ProvisioningOptions> options, ILogger<Program> log) =>
{
    try
    {
        return Results.Ok(new
        {
            requiresActivationCode = !options.Value.AllowRoomOnlyEnrollment,
            rooms = rooms.List().Select(r => new { id = r.Id, name = r.DisplayName })
        });
    }
    catch (InvalidOperationException ex)
    {
        log.LogError(ex, "호실 프로비저닝 목록을 읽지 못했습니다.");
        return Error("호실 설정 서버가 아직 준비되지 않았습니다.", StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/claims", (ClaimRequest? request, RoomRegistry rooms, IOptions<ProvisioningOptions> options, ILogger<Program> log) =>
{
    if (request is null || !RoomRegistry.IsValidId(request.RoomId) || !RoomRegistry.IsSafeDeviceValue(request.InstallationId) ||
        !RoomRegistry.IsSafeDeviceValue(request.MachineName))
    {
        return Error("호실 또는 설치 식별자가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }

    try
    {
        var result = rooms.Claim(request, options.Value.AllowRoomOnlyEnrollment);
        return result.Kind switch
        {
            ClaimResultKind.Success => Results.Ok(new
            {
                roomId = result.Assignment!.RoomId,
                displayName = result.Assignment.DisplayName,
                cloudflareHostname = result.Assignment.CloudflareHostname,
                cloudflareTunnelToken = result.Assignment.CloudflareTunnelToken,
                port = result.Assignment.Port,
                cloudflareProtocol = result.Assignment.CloudflareProtocol
            }),
            ClaimResultKind.UnknownRoom => Error("선택한 호실을 찾을 수 없습니다.", StatusCodes.Status404NotFound),
            ClaimResultKind.InvalidCode => Error("등록 코드가 올바르지 않습니다.", StatusCodes.Status403Forbidden),
            ClaimResultKind.AlreadyClaimed => Error("이 호실은 이미 다른 PC에 등록되어 있습니다. 관리자에게 초기화를 요청하세요.", StatusCodes.Status409Conflict),
            _ => Error("호실 설정을 적용할 수 없습니다.", StatusCodes.Status500InternalServerError)
        };
    }
    catch (InvalidOperationException ex)
    {
        log.LogError(ex, "호실 프로비저닝 요청을 처리하지 못했습니다.");
        return Error("호실 설정 서버가 아직 준비되지 않았습니다.", StatusCodes.Status503ServiceUnavailable);
    }
});

// A reset is intentionally admin-only. It is needed when a PC is replaced or the wrong room was
// chosen; the next install can then claim the released room again.
app.MapPost("/api/admin/rooms/{roomId}/release", (string roomId, HttpRequest request, RoomRegistry rooms,
    IOptions<ProvisioningOptions> options) =>
{
    if (!HasAdminToken(request, options.Value.AdminToken))
    {
        return Error("관리자 인증이 필요합니다.", StatusCodes.Status401Unauthorized);
    }
    if (!RoomRegistry.IsValidId(roomId))
    {
        return Error("호실 식별자가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }
    return rooms.Release(roomId)
        ? Results.NoContent()
        : Error("선택한 호실을 찾을 수 없습니다.", StatusCodes.Status404NotFound);
});

app.Run();

static IResult Error(string message, int statusCode) => Results.Json(new { error = message }, statusCode: statusCode);

static bool HasAdminToken(HttpRequest request, string? expected)
{
    if (string.IsNullOrWhiteSpace(expected) || !request.Headers.TryGetValue("X-Admin-Token", out var supplied))
    {
        return false;
    }
    var a = Encoding.UTF8.GetBytes(expected);
    var b = Encoding.UTF8.GetBytes(supplied.ToString());
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

public sealed class ProvisioningOptions
{
    public string RoomsFile { get; set; } = "data/rooms.json";

    /// <summary>
    /// Enables the exact "select room only" flow. Turn this on only while the service is protected
    /// to the school's trusted network; the default requires a per-room one-time enrollment code.
    /// </summary>
    public bool AllowRoomOnlyEnrollment { get; set; }

    /// <summary>Server-only administrator token for releasing an incorrectly claimed room.</summary>
    public string AdminToken { get; set; } = string.Empty;
}

public sealed class RoomRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _roomsFile;
    private RoomDocument? _document;

    public RoomRegistry(IOptions<ProvisioningOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.RoomsFile;
        _roomsFile = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
    }

    public IReadOnlyList<RoomSecret> List()
    {
        lock (_gate)
        {
            return LoadLocked().Rooms
                .Where(r => IsValidId(r.Id) && !string.IsNullOrWhiteSpace(r.DisplayName))
                .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)
                .Select(r => r.PublicCopy())
                .ToList();
        }
    }

    public ClaimResult Claim(ClaimRequest request, bool allowRoomOnlyEnrollment)
    {
        lock (_gate)
        {
            var document = LoadLocked();
            var room = document.Rooms.FirstOrDefault(r =>
                string.Equals(r.Id, request.RoomId, StringComparison.OrdinalIgnoreCase));
            if (room is null)
            {
                return new ClaimResult(ClaimResultKind.UnknownRoom);
            }

            if (!allowRoomOnlyEnrollment && !ActivationCodeHasher.Verify(request.ActivationCode, room.ActivationCodeHash))
            {
                return new ClaimResult(ClaimResultKind.InvalidCode);
            }

            if (!string.IsNullOrWhiteSpace(room.ClaimedInstallationId) &&
                !string.Equals(room.ClaimedInstallationId, request.InstallationId, StringComparison.Ordinal))
            {
                return new ClaimResult(ClaimResultKind.AlreadyClaimed);
            }
            if (string.IsNullOrWhiteSpace(room.CloudflareHostname) || string.IsNullOrWhiteSpace(room.CloudflareTunnelToken))
            {
                throw new InvalidOperationException($"호실 '{room.Id}'의 터널 설정이 비어 있습니다.");
            }

            room.ClaimedInstallationId = request.InstallationId;
            room.ClaimedMachineName = request.MachineName;
            room.ClaimedAtUtc = DateTimeOffset.UtcNow;
            SaveLocked(document);
            return new ClaimResult(ClaimResultKind.Success, new ProvisioningAssignment(
                room.Id, room.DisplayName, room.CloudflareHostname, room.CloudflareTunnelToken,
                room.Port is >= 1 and <= 65535 ? room.Port : 8787,
                NormalizeProtocol(room.CloudflareProtocol)));
        }
    }

    public bool Release(string roomId)
    {
        lock (_gate)
        {
            var document = LoadLocked();
            var room = document.Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            if (room is null)
            {
                return false;
            }
            room.ClaimedInstallationId = null;
            room.ClaimedMachineName = null;
            room.ClaimedAtUtc = null;
            SaveLocked(document);
            return true;
        }
    }

    public static bool IsValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsSafeDeviceValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => !char.IsControl(c));

    private RoomDocument LoadLocked()
    {
        if (_document is not null)
        {
            return _document;
        }
        if (!File.Exists(_roomsFile))
        {
            throw new InvalidOperationException($"호실 비밀 설정 파일이 없습니다: {_roomsFile}");
        }
        var parsed = JsonSerializer.Deserialize<RoomDocument>(File.ReadAllText(_roomsFile), Json);
        if (parsed?.Rooms is null || parsed.Rooms.Count == 0)
        {
            throw new InvalidOperationException("호실 비밀 설정 파일이 비어 있거나 올바르지 않습니다.");
        }
        _document = parsed;
        return _document;
    }

    private void SaveLocked(RoomDocument document)
    {
        var directory = Path.GetDirectoryName(_roomsFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporary = _roomsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, _roomsFile, overwrite: true);
    }

    private static string NormalizeProtocol(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "quic" => "quic",
        "auto" => "auto",
        _ => "http2"
    };
}

public sealed class RoomDocument
{
    public List<RoomSecret> Rooms { get; set; } = new();
}

/// <summary>
/// Server-side room record. This file contains long-lived Cloudflare tokens and must remain outside
/// source control, on a volume readable only by the provisioning service account.
/// </summary>
public sealed class RoomSecret
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CloudflareHostname { get; set; } = string.Empty;
    public string CloudflareTunnelToken { get; set; } = string.Empty;
    public int Port { get; set; } = 8787;
    public string CloudflareProtocol { get; set; } = "http2";
    public string ActivationCodeHash { get; set; } = string.Empty;
    public string? ClaimedInstallationId { get; set; }
    public string? ClaimedMachineName { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }

    public RoomSecret PublicCopy() => new()
    {
        Id = Id,
        DisplayName = DisplayName
    };
}

public sealed record ClaimRequest(string RoomId, string InstallationId, string MachineName, string? ActivationCode);

public sealed record ProvisioningAssignment(
    string RoomId,
    string DisplayName,
    string CloudflareHostname,
    string CloudflareTunnelToken,
    int Port,
    string CloudflareProtocol);

public enum ClaimResultKind
{
    Success,
    UnknownRoom,
    InvalidCode,
    AlreadyClaimed
}

public sealed record ClaimResult(ClaimResultKind Kind, ProvisioningAssignment? Assignment = null);

/// <summary>PBKDF2 hashes per-room enrollment codes; the server never stores a usable code.</summary>
public static class ActivationCodeHasher
{
    private const int Iterations = 310_000;
    private const string Version = "pbkdf2-sha256-v1";

    public static string Hash(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("등록 코드는 비어 있을 수 없습니다.", nameof(code));
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Version}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? code, string? encoded)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Version || !int.TryParse(parts[1], out var iterations) || iterations < 100_000)
        {
            return false;
        }
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
