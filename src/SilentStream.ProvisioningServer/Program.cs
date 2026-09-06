using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SilentStream.Core.Recovery;
using SilentStream.Core.Remote.WebPush;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Operational helper: generates the PBKDF2 value stored in data/rooms.json without ever writing
// the plaintext enrollment code to disk. Example:
// dotnet run --project src/SilentStream.ProvisioningServer -- hash-code "long-random-code"
if (args is ["hash-code", var code])
{
    Console.WriteLine(ActivationCodeHasher.Hash(code));
    return;
}

// Generates the one shared VAPID keypair copied into the server-only rooms.json document.
// It deliberately prints the private scalar only for this explicit administrator command.
if (args is ["generate-vapid"])
{
    var keys = VapidKeyMaterial.Generate();
    Console.WriteLine(JsonSerializer.Serialize(new SharedVapidKey(
        keys.PublicKeyBase64Url, Base64Url.Encode(keys.PrivateKey)), new JsonSerializerOptions { WriteIndented = true }));
    return;
}

builder.Services.Configure<ProvisioningOptions>(builder.Configuration.GetSection("Provisioning"));
builder.Services.Configure<ControlPlaneOptions>(builder.Configuration.GetSection("ControlPlane"));
builder.Services.PostConfigure<ControlPlaneOptions>(options =>
{
    options.StateFile = ContentPath(options.StateFile);
    options.PreparedPackagePath = ContentPath(options.PreparedPackagePath);
    options.InstallerPath = ContentPath(options.InstallerPath);
});
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<RecoveryVaultStore>();
builder.Services.AddSingleton<ControlPlaneStore>();
builder.Services.AddSingleton<ControlPlaneAuthorizer>();
builder.Services.AddSingleton<CloudflareAccessJwtValidator>();
builder.Services.AddSingleton<DeviceConnectionHub>();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), payment=(), usb=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'; img-src 'self' data:; connect-src 'self' wss:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    if (context.Request.Path.StartsWithSegments("/control") || context.Request.Path.StartsWithSegments("/install")
        || context.Request.Path.StartsWithSegments("/api/control") || context.Request.Path.StartsWithSegments("/api/install"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<IOptions<ControlPlaneOptions>>().Value;
    var deviceHost = Uri.TryCreate(options.DeviceBaseUri, UriKind.Absolute, out var deviceUri) ? deviceUri.Host : string.Empty;
    var deviceRequest = context.Request.Path.StartsWithSegments("/api/device");
    var onDeviceHost = string.Equals(context.Request.Host.Host, deviceHost, StringComparison.OrdinalIgnoreCase);
    var localDevelopment = app.Environment.IsDevelopment()
        && System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None);
    var deviceHealth = context.Request.Path == "/health" || context.Request.Path == "/ready";
    if (!localDevelopment && (deviceRequest != onDeviceHost)
        && (deviceRequest || onDeviceHost && !deviceHealth))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseMiddleware<CloudflareAccessAuthentication>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/ready", (IOptions<ControlPlaneOptions> configured) =>
{
    var options = configured.Value;
    if (!options.Enabled) return Results.Ok(new { status = "ready", controlPlane = "disabled" });
    var missing = new List<string>();
    if (!Uri.TryCreate(options.PublicBaseUri, UriKind.Absolute, out var publicUri)
        || publicUri.Scheme != Uri.UriSchemeHttps) missing.Add("ControlPlane:PublicBaseUri");
    if (!Uri.TryCreate(options.DeviceBaseUri, UriKind.Absolute, out var deviceUri)
        || deviceUri.Scheme != Uri.UriSchemeHttps) missing.Add("ControlPlane:DeviceBaseUri");
    if (string.IsNullOrWhiteSpace(options.AccessTeamDomain)) missing.Add("ControlPlane:AccessTeamDomain");
    if (string.IsNullOrWhiteSpace(options.AccessAudience) && options.AccessAudiences.Count == 0)
        missing.Add("ControlPlane:AccessAudience(s)");
    if (options.AdminEmails.Count == 0 && options.RoomManagers.Count == 0) missing.Add("ControlPlane RBAC users");
    if (!File.Exists(options.PreparedPackagePath)
        || !string.Equals(Path.GetExtension(options.PreparedPackagePath), ".zip", StringComparison.OrdinalIgnoreCase))
        missing.Add("prepared package");
    if (!File.Exists(options.InstallerPath)
        || !string.Equals(Path.GetExtension(options.InstallerPath), ".exe", StringComparison.OrdinalIgnoreCase))
        missing.Add("bootstrap installer");
    return missing.Count == 0
        ? Results.Ok(new { status = "ready", controlPlane = "ready" })
        : Results.Json(new { status = "not-ready", missing }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

if (app.Services.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.Enabled)
    app.MapControlPlane();

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
                cloudflareProtocol = result.Assignment.CloudflareProtocol,
                sharedVapid = result.Assignment.SharedVapid
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

// Updates installed versions that predate shared multi-room push. The per-room tunnel token is
// already held DPAPI-protected by that exact installation, making it a high-entropy proof without
// reopening the one-time enrollment-code UI after every release.
app.MapPost("/api/assignments/refresh-vapid", (VapidRefreshRequest? request, RoomRegistry rooms, ILogger<Program> log) =>
{
    if (request is null || !RoomRegistry.IsValidId(request.RoomId) ||
        !RoomRegistry.IsSafeDeviceValue(request.InstallationId) || !RoomRegistry.IsSafeSecret(request.TunnelToken))
    {
        return Error("등록 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }

    try
    {
        var result = rooms.RefreshSharedVapid(request);
        return result.Kind switch
        {
            VapidRefreshResultKind.Success => Results.Ok(new { sharedVapid = result.SharedVapid }),
            VapidRefreshResultKind.NotConfigured => Results.NoContent(),
            _ => Error("등록 정보를 확인할 수 없습니다.", StatusCodes.Status401Unauthorized)
        };
    }
    catch (InvalidOperationException ex)
    {
        // Never include the request or tunnel token in logs; only server-side configuration faults
        // are useful to the operator here.
        log.LogError(ex, "공유 알림 키 갱신 요청을 처리하지 못했습니다.");
        return Error("호실 설정 서버가 아직 준비되지 않았습니다.", StatusCodes.Status503ServiceUnavailable);
    }
});

// Opaque recovery-vault endpoints. All device writes prove possession of the per-room tunnel
// token; the backup's contents are already encrypted to a Windows CNG/TPM key before upload.
app.MapPost("/api/recovery/register", (RecoveryRegistrationRequest? request, RecoveryVaultStore vault) =>
{
    if (request is null || !RoomRegistry.IsValidId(request.RoomId) ||
        !RoomRegistry.IsSafeDeviceValue(request.InstallationId) || !RoomRegistry.IsSafeSecret(request.TunnelToken))
    {
        return Error("복구 등록 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }
    return vault.Register(request)
        ? Results.NoContent()
        : Error("복구 키를 등록할 수 없습니다.", StatusCodes.Status401Unauthorized);
});

app.MapPost("/api/recovery/snapshots", (RecoverySnapshotUploadRequest? request, RecoveryVaultStore vault) =>
{
    if (request is null || !RoomRegistry.IsValidId(request.RoomId) ||
        !RoomRegistry.IsSafeDeviceValue(request.InstallationId) || !RoomRegistry.IsSafeSecret(request.TunnelToken))
    {
        return Error("복구 백업 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }
    return vault.StoreSnapshot(request)
        ? Results.NoContent()
        : Error("복구 백업을 저장할 수 없습니다.", StatusCodes.Status401Unauthorized);
});

app.MapPost("/api/recovery/challenges", (RecoveryChallengeRequest? request, RecoveryVaultStore vault) =>
{
    if (request is null) return Error("복구 키 정보가 없습니다.", StatusCodes.Status400BadRequest);
    var challenge = vault.CreateChallenge(request.KeyId);
    return challenge is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            challengeId = challenge.ChallengeId,
            nonce = Convert.ToBase64String(challenge.Nonce),
            expiresAtUtc = challenge.ExpiresAtUtc
        });
});

app.MapPost("/api/recovery/restore", (RecoveryRestoreRequest? request, RecoveryVaultStore vault) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.ChallengeId) || string.IsNullOrWhiteSpace(request.Signature))
        return Error("복구 확인 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    try
    {
        var snapshot = vault.Restore(request.ChallengeId, Convert.FromBase64String(request.Signature));
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }
    catch (FormatException)
    {
        return Error("복구 서명이 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    }
});

// The administrator credential is accepted only by the provisioning server. The issued command
// is room/install-specific and must then be consumed by that exact installed device.
app.MapPost("/api/admin/removal-commands", (RemovalIssueRequest? request, HttpRequest http, RecoveryVaultStore vault,
    IOptions<ProvisioningOptions> options) =>
{
    if (!HasAdminToken(http, options.Value.AdminToken))
        return Error("관리자 인증이 필요합니다.", StatusCodes.Status401Unauthorized);
    if (request is null || !RoomRegistry.IsValidId(request.RoomId))
        return Error("호실 식별자가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    var result = vault.IssueRemoval(request.RoomId);
    return result.Kind switch
    {
        RemovalCommandResultKind.Success => Results.Ok(new { commandId = result.CommandId, expiresAtUtc = result.ExpiresAtUtc }),
        RemovalCommandResultKind.RecoveryBackupUnavailable => Error("최신 복구 백업이 확인되지 않아 제거 명령을 발급할 수 없습니다.", StatusCodes.Status409Conflict),
        _ => Error("등록된 기기를 찾을 수 없습니다.", StatusCodes.Status404NotFound)
    };
});

app.MapPost("/api/device-removal-commands/{commandId}/consume", (string commandId, RemovalConsumeRequest? request,
    RecoveryVaultStore vault) =>
{
    if (request is null || !RoomRegistry.IsValidId(request.RoomId) ||
        !RoomRegistry.IsSafeDeviceValue(request.InstallationId) || !RoomRegistry.IsSafeSecret(request.TunnelToken))
        return Error("삭제 명령 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    var result = vault.ConsumeRemoval(commandId, request.RoomId, request.InstallationId, request.TunnelToken);
    return result.Kind switch
    {
        RemovalConsumeResultKind.Success => Results.Ok(new { completionToken = result.CompletionToken }),
        RemovalConsumeResultKind.NotFound => Results.NotFound(),
        _ => Error("삭제 명령을 실행할 권한이 없습니다.", StatusCodes.Status401Unauthorized)
    };
});

app.MapPost("/api/device-removal-commands/{commandId}/complete", (string commandId, RemovalCompleteRequest? request,
    RecoveryVaultStore vault) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.CompletionToken))
        return Error("완료 확인 정보가 올바르지 않습니다.", StatusCodes.Status400BadRequest);
    return vault.CompleteRemoval(commandId, request.CompletionToken) ? Results.NoContent() : Results.NotFound();
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

string ContentPath(string value) => string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)
    ? value
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, value));

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

    /// <summary>Protected server volume holding encrypted recovery envelopes and removal state.</summary>
    public string RecoveryDirectory { get; set; } = "data/recovery";
    public int RecoverySnapshotRetainCount { get; set; } = 3;
    public int RecoverySnapshotMaxBytes { get; set; } = 16 * 1024 * 1024;
    public int RecoveryChallengeSeconds { get; set; } = 60;
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
                NormalizeProtocol(room.CloudflareProtocol), GetSharedVapidLocked(document)));
        }
    }

    public VapidRefreshResult RefreshSharedVapid(VapidRefreshRequest request)
    {
        lock (_gate)
        {
            var document = LoadLocked();
            var room = document.Rooms.FirstOrDefault(r =>
                string.Equals(r.Id, request.RoomId, StringComparison.OrdinalIgnoreCase));
            if (room is null || !string.Equals(room.ClaimedInstallationId, request.InstallationId, StringComparison.Ordinal) ||
                !FixedTimeEquals(room.CloudflareTunnelToken, request.TunnelToken))
            {
                return new VapidRefreshResult(VapidRefreshResultKind.Unauthorized);
            }

            var shared = GetSharedVapidLocked(document);
            return shared is null
                ? new VapidRefreshResult(VapidRefreshResultKind.NotConfigured)
                : new VapidRefreshResult(VapidRefreshResultKind.Success, shared);
        }
    }

    public bool VerifyInstalledDevice(string roomId, string installationId, string tunnelToken)
    {
        lock (_gate)
        {
            var room = LoadLocked().Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            return room is not null && string.Equals(room.ClaimedInstallationId, installationId, StringComparison.Ordinal) &&
                FixedTimeEquals(room.CloudflareTunnelToken, tunnelToken);
        }
    }

    public bool RegisterRecoveryIdentity(string roomId, string installationId, string tunnelToken, string keyId, string protectionLevel)
    {
        lock (_gate)
        {
            var document = LoadLocked();
            var room = document.Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            if (room is null || !string.Equals(room.ClaimedInstallationId, installationId, StringComparison.Ordinal) ||
                !FixedTimeEquals(room.CloudflareTunnelToken, tunnelToken)) return false;
            // A different existing recovery identity is never overwritten by a running device.
            if (!string.IsNullOrWhiteSpace(room.RecoveryKeyId) &&
                !string.Equals(room.RecoveryKeyId, keyId, StringComparison.Ordinal)) return false;
            // Room data deliberately retains only the public-key fingerprint. The public key
            // needed for proof-of-possession lives with the protected recovery vault instead.
            room.RecoveryKeyId = keyId;
            room.RecoveryProtectionLevel = protectionLevel;
            SaveLocked(document);
            return true;
        }
    }

    public bool IsRecoveryIdentityForDevice(string roomId, string installationId, string tunnelToken, string keyId)
    {
        lock (_gate)
        {
            var room = LoadLocked().Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            return room is not null && string.Equals(room.ClaimedInstallationId, installationId, StringComparison.Ordinal) &&
                FixedTimeEquals(room.CloudflareTunnelToken, tunnelToken) &&
                string.Equals(room.RecoveryKeyId, keyId, StringComparison.Ordinal);
        }
    }

    public string? GetRecoveryKeyId(string roomId)
    {
        lock (_gate)
        {
            var room = LoadLocked().Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            return room is null || string.IsNullOrWhiteSpace(room.ClaimedInstallationId)
                ? null
                : room.RecoveryKeyId;
        }
    }

    public RoomSecret? GetClaimedRoom(string roomId)
    {
        lock (_gate)
        {
            var room = LoadLocked().Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.OrdinalIgnoreCase));
            return room is null || string.IsNullOrWhiteSpace(room.ClaimedInstallationId) ? null : room.PublicClaimCopy();
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

    public static bool IsSafeSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && value.All(c => !char.IsControl(c));

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

    private static SharedVapidKey? GetSharedVapidLocked(RoomDocument document)
    {
        if (document.SharedVapid is null)
        {
            return null; // shared push remains optional for legacy / single-room deployments
        }
        if (!document.SharedVapid.TryGetKeys(out _))
        {
            throw new InvalidOperationException("공유 VAPID 키가 올바르지 않습니다.");
        }
        return document.SharedVapid;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed class RoomDocument
{
    /// <summary>
    /// One VAPID identity shared by every room in this deployment. The private key is server-only
    /// configuration and is returned solely through a successful claim/refresh over HTTPS.
    /// </summary>
    public SharedVapidKey? SharedVapid { get; set; }

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
    public string? RecoveryKeyId { get; set; }
    public string? RecoveryProtectionLevel { get; set; }

    public RoomSecret PublicCopy() => new()
    {
        Id = Id,
        DisplayName = DisplayName
    };

    public RoomSecret PublicClaimCopy() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ClaimedInstallationId = ClaimedInstallationId
    };
}

public sealed record ClaimRequest(string RoomId, string InstallationId, string MachineName, string? ActivationCode);

public sealed record VapidRefreshRequest(string RoomId, string InstallationId, string TunnelToken);
public sealed record RemovalIssueRequest(string RoomId);
public sealed record RemovalConsumeRequest(string RoomId, string InstallationId, string TunnelToken);
public sealed record RemovalCompleteRequest(string CompletionToken);

public sealed record ProvisioningAssignment(
    string RoomId,
    string DisplayName,
    string CloudflareHostname,
    string CloudflareTunnelToken,
    int Port,
    string CloudflareProtocol,
    SharedVapidKey? SharedVapid);

public enum ClaimResultKind
{
    Success,
    UnknownRoom,
    InvalidCode,
    AlreadyClaimed
}

public sealed record ClaimResult(ClaimResultKind Kind, ProvisioningAssignment? Assignment = null);

public enum VapidRefreshResultKind
{
    Success,
    NotConfigured,
    Unauthorized
}

public sealed record VapidRefreshResult(VapidRefreshResultKind Kind, SharedVapidKey? SharedVapid = null);

/// <summary>
/// Base64url representation of an RFC 8292 P-256 VAPID keypair. This lives only in the server's
/// non-versioned room document and in a Windows user's DPAPI-protected local VAPID store.
/// </summary>
public sealed record SharedVapidKey(string PublicKey, string PrivateKey)
{
    public bool TryGetKeys(out VapidKeys keys)
    {
        if (string.IsNullOrWhiteSpace(PublicKey) || string.IsNullOrWhiteSpace(PrivateKey))
        {
            keys = null!;
            return false;
        }
        try
        {
            keys = new VapidKeys(Base64Url.Decode(PublicKey), Base64Url.Decode(PrivateKey));
            return VapidKeyMaterial.IsValid(keys);
        }
        catch (FormatException)
        {
            keys = null!;
            return false;
        }
    }
}

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
