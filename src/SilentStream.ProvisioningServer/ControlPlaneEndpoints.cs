using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SilentStream.Inspection;

public static class ControlPlaneEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapControlPlane(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context, RoomRegistry rooms, IOptions<ControlPlaneOptions> configured) =>
        {
            var label = context.Request.Host.Host.Split('.', 2)[0];
            var room = RoomId.Normalize(label);
            return KnownRooms(rooms, configured.Value).Contains(room, StringComparer.OrdinalIgnoreCase)
                ? Results.Redirect($"/control/?room={Uri.EscapeDataString(room)}")
                : Results.Redirect("/control/");
        });
        app.MapGet("/control", () => Results.Redirect("/control/"));
        app.MapGet("/control/", (IWebHostEnvironment environment) =>
            Results.File(Path.Combine(environment.WebRootPath, "control", "index.html"), "text/html; charset=utf-8"));

        app.MapGet("/api/control/session", (HttpContext context, ControlPlaneAuthorizer authorizer,
            RoomRegistry rooms, IOptions<ControlPlaneOptions> configured) =>
        {
            var known = KnownRooms(rooms, configured.Value);
            var allowed = authorizer.RoomsFor(context.User, known);
            return Results.Json(new
            {
                email = ControlPlaneAuthorizer.Email(context.User),
                admin = allowed.Any(room => authorizer.RoleFor(context.User, room) == ControlPlaneRole.Admin),
                rooms = allowed
            }, Json);
        });

        app.MapGet("/api/control/rooms", (HttpContext context, ControlPlaneAuthorizer authorizer,
            ControlPlaneStore store, IOptions<ControlPlaneOptions> configured, RoomRegistry rooms) =>
        {
            var allowed = authorizer.RoomsFor(context.User, KnownRooms(rooms, configured.Value));
            var offline = TimeSpan.FromSeconds(Math.Clamp(configured.Value.DeviceOfflineSeconds, 15, 600));
            var devices = store.ListDevices(allowed, offline).Select(device => new
            {
                device.RoomId, device.Registered, device.Online, device.DeviceId, device.InstallationId,
                device.MachineName, device.AgentVersion, device.RegisteredAtUtc, device.LastSeenAtUtc,
                device.App, device.Audio, device.Inspection, device.Recovery,
                role = authorizer.RoleFor(context.User, device.RoomId).ToString().ToLowerInvariant()
            });
            return Results.Json(devices, Json);
        });

        app.MapPost("/api/control/rooms/{roomId}/actions/{action}", async (HttpContext context,
            string roomId, string action, ControlPlaneAuthorizer authorizer, DeviceConnectionHub hub) =>
        {
            if (!SameOrigin(context.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            roomId = RoomId.Normalize(roomId);
            if (!RoomId.IsValid(roomId) || !CentralCommandPolicy.TryResolve(action, out var target))
                return Results.NotFound();
            var role = authorizer.RoleFor(context.User, roomId);
            if (role < target.MinimumRole) return Results.StatusCode(StatusCodes.Status403Forbidden);
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                target.Method, target.Path,
                body = await ReadOptionalBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false)
            }, Json);
            var requestedBy = ControlPlaneAuthorizer.Email(context.User) ?? "unknown";
            var result = await hub.IssueAsync(roomId, action, payload, requestedBy, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(result, Json, statusCode: result.StatusCode);
        });

        app.MapGet("/install/{roomId}", (HttpContext context, string roomId,
            ControlPlaneAuthorizer authorizer) =>
        {
            roomId = RoomId.Normalize(roomId);
            if (!RoomId.IsValid(roomId)) return Results.NotFound();
            if (authorizer.RoleFor(context.User, roomId) == ControlPlaneRole.None)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var encoded = WebUtility.HtmlEncode(roomId);
            return Results.Content($$"""
                <!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>{{encoded}} 설치</title><style>body{font-family:system-ui;max-width:640px;margin:10vh auto;padding:24px;background:#f5f7fb;color:#18212f}.card{background:white;border-radius:18px;padding:28px;box-shadow:0 8px 35px #17223b1a}a{display:inline-block;background:#1769e0;color:white;text-decoration:none;padding:14px 20px;border-radius:10px;font-weight:700}small{color:#5f6b7a}</style></head>
                <body><main class="card"><h1>{{encoded}} 자동 설치</h1><p>Google 로그인으로 설치 권한이 확인되었습니다. 설치파일을 실행하고 UAC에서 <b>예</b>를 누르면 앱·원격 에이전트·자동 점검이 한 번에 구성됩니다.</p>
                <p><a href="/install/{{Uri.EscapeDataString(roomId)}}/download">설치파일 다운로드</a></p><small>설치파일은 이 호실명을 파일명에 포함하며, 실행 시 현재 Google 세션으로 일회용 등록을 완료합니다.</small></main></body></html>
                """, "text/html; charset=utf-8");
        });

        app.MapGet("/install/{roomId}/download", (HttpContext context, string roomId,
            ControlPlaneAuthorizer authorizer, IOptions<ControlPlaneOptions> configured) =>
        {
            roomId = RoomId.Normalize(roomId);
            if (!RoomId.IsValid(roomId)) return Results.NotFound();
            if (authorizer.RoleFor(context.User, roomId) == ControlPlaneRole.None)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var path = configured.Value.InstallerPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("중앙 설치파일이 아직 게시되지 않았습니다.", statusCode: 503);
            return Results.File(Path.GetFullPath(path), "application/vnd.microsoft.portable-executable",
                $"SilentStream-Install-{roomId}.exe", enableRangeProcessing: true);
        });

        app.MapGet("/api/install/authorize", (HttpContext context, string room, string state, string callback,
            ControlPlaneAuthorizer authorizer, ControlPlaneStore store, IOptions<ControlPlaneOptions> configured) =>
        {
            room = RoomId.Normalize(room);
            if (!RoomId.IsValid(room) || state.Length is < 20 or > 200 || !SafeLoopbackCallback(callback, out var target))
                return Results.BadRequest(new { error = "설치 승인 요청이 올바르지 않습니다." });
            if (authorizer.RoleFor(context.User, room) == ControlPlaneRole.None)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var email = ControlPlaneAuthorizer.Email(context.User)!;
            var deviceBaseUri = (string.IsNullOrWhiteSpace(configured.Value.DeviceBaseUri)
                ? configured.Value.PublicBaseUri : configured.Value.DeviceBaseUri).TrimEnd('/');
            if (!SafeServiceBase(deviceBaseUri))
                return Results.Problem("중앙 장치 서버 주소가 올바르지 않습니다.", statusCode: 503);
            var code = store.CreateEnrollment(room, email,
                TimeSpan.FromMinutes(Math.Clamp(configured.Value.EnrollmentLifetimeMinutes, 2, 30)));
            var separator = target.Query.Length == 0 ? "?" : "&";
            var redirect = target + separator + "code=" + Uri.EscapeDataString(code)
                + "&state=" + Uri.EscapeDataString(state) + "&room=" + Uri.EscapeDataString(room)
                + "&enroll=" + Uri.EscapeDataString(deviceBaseUri + "/api/device/enroll");
            return Results.Redirect(redirect);
        });

        app.MapPost("/api/device/enroll", (CentralEnrollmentRedeemRequest request,
            ControlPlaneStore store, IOptions<ControlPlaneOptions> configured) =>
        {
            var package = store.Package(configured.Value);
            if (package.Length <= 0 || !string.Equals(Path.GetExtension(package.Path), ".zip", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("설치 패키지가 아직 게시되지 않았습니다.", statusCode: 503);
            var result = store.Redeem(request, configured.Value);
            return result is null ? Results.Unauthorized() : Results.Json(result, Json);
        });

        app.MapGet("/api/device/package", (HttpContext context, ControlPlaneStore store,
            IOptions<ControlPlaneOptions> configured) =>
        {
            if (!TryAuthenticateDevice(context, store, out _)) return Results.Unauthorized();
            var package = store.Package(configured.Value);
            if (string.IsNullOrWhiteSpace(package.Path) || !File.Exists(package.Path)
                || !string.Equals(Path.GetExtension(package.Path), ".zip", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("설치 패키지가 아직 게시되지 않았습니다.", statusCode: 503);
            context.Response.Headers["X-SilentStream-Sha256"] = package.Hash;
            return Results.File(package.Path, "application/zip", "SilentStream-prepared-package.zip",
                enableRangeProcessing: true);
        });

        app.MapGet("/api/device/self", (HttpContext context, ControlPlaneStore store, DeviceConnectionHub hub) =>
        {
            if (!TryAuthenticateDevice(context, store, out var identity)) return Results.Unauthorized();
            return Results.Ok(new
            {
                roomId = identity!.RoomId,
                deviceId = identity.DeviceId,
                connected = hub.IsConnected(identity.RoomId, identity.DeviceId),
                reporting = store.HasReported(identity)
            });
        });

        app.Map("/api/device/connect", async context =>
        {
            var store = context.RequestServices.GetRequiredService<ControlPlaneStore>();
            if (!context.WebSockets.IsWebSocketRequest || !TryAuthenticateDevice(context, store, out var identity))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await context.RequestServices.GetRequiredService<DeviceConnectionHub>()
                .AcceptAsync(identity!, socket, context.RequestAborted).ConfigureAwait(false);
        });
    }

    public static bool TryAuthenticateDevice(HttpContext context, ControlPlaneStore store, out DeviceIdentity? identity)
    {
        identity = null;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Device ", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = header["Device ".Length..].Split(':', 3);
        if (parts.Length != 3) return false;
        identity = store.Authenticate(parts[0], parts[1], parts[2]);
        return identity is not null;
    }

    private static IReadOnlyList<string> KnownRooms(RoomRegistry rooms, ControlPlaneOptions options)
    {
        IEnumerable<string> legacy;
        try { legacy = rooms.List().Select(room => room.Id); }
        catch (InvalidOperationException) { legacy = []; }
        return legacy.Concat(RoomId.KnownDefaults).Concat(options.RoomManagers.Keys)
            .Select(RoomId.Normalize).Where(RoomId.IsValid)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    }

    private static async Task<JsonElement?> ReadOptionalBodyAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength is null or 0) return null;
        if (request.ContentLength > 1024 * 1024) throw new BadHttpRequestException("Request body is too large.");
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: token).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new BadHttpRequestException("Request body must be JSON."); }
    }

    private static bool SafeLoopbackCallback(string value, out Uri target)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out target!) || target.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment)) return false;
        return target.IsLoopback && target.Port is > 1024 and <= 65535;
    }

    private static bool SafeServiceBase(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp);

    private static bool SameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var value)
            || !Uri.TryCreate(value.ToString(), UriKind.Absolute, out var origin)) return false;
        var local = IPAddress.IsLoopback(request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.None);
        var schemeOk = string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || local && string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
        var expectedPort = request.Host.Port ?? (origin.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return schemeOk && string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == expectedPort;
    }
}
