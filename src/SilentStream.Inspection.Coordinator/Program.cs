using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;

var consoleMode = args.Any(value => value.Equals("--console", StringComparison.Ordinal));
var rootArgument = args.FirstOrDefault(value => value.StartsWith("--inspection-root=", StringComparison.Ordinal));
var rootOverride = rootArgument is null ? null : rootArgument["--inspection-root=".Length..];
var hostArgs = args.Where(value => !value.Equals("--console", StringComparison.Ordinal)
    && !value.StartsWith("--inspection-root=", StringComparison.Ordinal)).ToArray();
var optionsStore = new CoordinatorOptionsStore(string.IsNullOrWhiteSpace(rootOverride) ? null : rootOverride);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
if (!consoleMode)
    builder.Host.UseWindowsService(service => service.ServiceName = "SilentStream Inspection Coordinator");
builder.WebHost.UseUrls($"http://{optionsStore.Current.BindAddress}:{optionsStore.Current.Port}");
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 270L * 1024 * 1024);
builder.Services.AddSingleton(optionsStore);
builder.Services.AddSingleton<InspectionStore>();
builder.Services.AddSingleton<InspectionAuthenticator>();
builder.Services.AddSingleton<ControlLeaseManager>();
builder.Services.AddSingleton<ConfirmationManager>();
builder.Services.AddSingleton<StartupUpdateGate>();
builder.Services.AddSingleton<SessionAgentClient>();
builder.Services.AddSingleton<ISessionAgentClient>(services => services.GetRequiredService<SessionAgentClient>());
builder.Services.AddSingleton<BrokerClient>();
builder.Services.AddSingleton<IBrokerClient>(services => services.GetRequiredService<BrokerClient>());
builder.Services.AddHttpClient();
builder.Services.AddHostedService<PreseedBundleHostedService>();
builder.Services.AddHostedService<AgentUpdateHostedService>();
builder.Services.AddHostedService<StartupInspectionHostedService>();
builder.Services.AddHostedService<InspectionEngine>();
builder.Services.AddHostedService<InspectionStatusReporter>();
builder.Services.AddHostedService<CentralControlAgentHostedService>();
builder.Services.AddHostedService<CloudflaredHostedService>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self' ws: wss:";
    context.Response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), payment=(), usb=()";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/inspect")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrWhiteSpace(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    var path = context.Request.Path;
    var protectedPath = path.StartsWithSegments("/inspect/api") || path.StartsWithSegments("/inspect/ws");
    var publicPath = path == "/inspect/api/v1/pair" || path == "/inspect/api/v1/health"
        || path == "/inspect/internal/v1/startup/rerun";
    if (!protectedPath || publicPath) { await next(); return; }
    var auth = context.RequestServices.GetRequiredService<InspectionAuthenticator>();
    var principal = auth.Authenticate(context);
    if (principal is null) { context.Response.StatusCode = 401; return; }
    context.User = principal;
    await next();
});

app.MapGet("/inspect/api/v1/health", (CoordinatorOptionsStore options) => Results.Json(new
{
    ok = true, version = InspectionProtocol.AgentVersion, room = options.Current.Room, roomId = options.Current.RoomId,
    session = Environment.UserInteractive, timestampUtc = DateTimeOffset.UtcNow
}, InspectionProtocol.Json));

app.MapPost("/inspect/internal/v1/startup/rerun", (HttpContext context, InspectionStore store,
    StartupUpdateGate startup, CoordinatorOptionsStore options) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null || !System.Net.IPAddress.IsLoopback(remoteAddress)) return Results.Unauthorized();
    var supplied = context.Request.Headers["X-SilentStream-Inspection-Bridge"].ToString();
    var expected = options.UnprotectMachine(options.Current.AppApiTokenMachineEnc);
    if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(expected)
        || !InspectionToken.Matches(supplied, InspectionToken.Hash(expected))) return Results.Unauthorized();
    return QueueStartupRerun(store, startup, options, "app:inspection-bridge");
});

app.MapGet("/inspect/api/v1/startup", (CoordinatorOptionsStore options, StartupUpdateGate startup, InspectionStore store) =>
{
    var snapshot = startup.Snapshot;
    return Results.Json(new
    {
        room = options.Current.Room,
        roomId = options.Current.RoomId,
        managed = options.Current.ManagedRooms,
        enabled = options.Current.StartupInspectionEnabled,
        state = snapshot,
        run = snapshot.RunId is null ? null : store.GetRunSnapshot(snapshot.RunId)
    }, InspectionProtocol.Json);
});
app.MapPost("/inspect/api/v1/startup/rerun", (HttpContext context, InspectionStore store,
    StartupUpdateGate startup, CoordinatorOptionsStore options) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return QueueStartupRerun(store, startup, options, Owner(context));
});

app.MapPost("/inspect/api/v1/pair", (HttpContext context, PairRequest request, InspectionAuthenticator auth, InspectionStore store) =>
{
    var connectionAddress = context.Connection.RemoteIpAddress;
    var forwarded = System.Net.IPAddress.IsLoopback(connectionAddress ?? System.Net.IPAddress.None)
        ? context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() : null;
    var remote = System.Net.IPAddress.TryParse(forwarded, out var forwardedAddress)
        ? forwardedAddress.ToString() : connectionAddress?.ToString() ?? "unknown";
    var result = auth.Pair(remote, request);
    store.Audit("pairing", "pair", result is null ? "denied" : "ok", remote);
    return result is null ? Results.Unauthorized() : Results.Json(result, InspectionProtocol.Json);
});

app.MapGet("/inspect/api/v1/me", (HttpContext context, CoordinatorOptionsStore options) => Results.Json(new
{
    tokenId = Owner(context), label = context.User.Identity?.Name,
    role = context.User.FindFirstValue(ClaimTypes.Role)?.ToLowerInvariant(), room = options.Current.Room,
    hostname = options.Current.CloudflareHostname
}, InspectionProtocol.Json));

app.MapGet("/inspect/api/v1/playlists", async (HttpContext context, CoordinatorOptionsStore options,
    IHttpClientFactory httpFactory) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var appToken = options.UnprotectMachine(options.Current.AppApiTokenMachineEnc);
    if (string.IsNullOrWhiteSpace(appToken))
        return Results.Json(new { error = "앱 점검 연결 권한을 다시 등록해야 합니다." },
            InspectionProtocol.Json, statusCode: StatusCodes.Status503ServiceUnavailable);

    var query = new List<string>();
    foreach (var name in new[] { "from", "to" })
    {
        var value = context.Request.Query[name].ToString();
        if (!string.IsNullOrWhiteSpace(value))
            query.Add($"{name}={Uri.EscapeDataString(value)}");
    }
    var uri = "http://127.0.0.1:8787/api/inspection/playlists"
        + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));

    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appToken);
        request.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", appToken);
        using var response = await httpFactory.CreateClient().SendAsync(request, context.RequestAborted);
        var payload = await response.Content.ReadAsStringAsync(context.RequestAborted);
        return Results.Text(payload, "application/json; charset=utf-8", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException)
        && !context.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new { error = "강의실 앱이 아직 실행되지 않았습니다." },
            InspectionProtocol.Json, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/inspect/api/v1/tokens", (HttpContext context, CoordinatorOptionsStore options) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Json(options.Current.Tokens.Select(t => new { tokenId=t.Hash[..Math.Min(12,t.Hash.Length)],role=t.Role.ToString().ToLowerInvariant(),t.Label,t.CreatedAtUtc,t.ExpiresAtUtc,t.RevokedAtUtc,t.LastUsedAtUtc }), InspectionProtocol.Json);
});
app.MapDelete("/inspect/api/v1/tokens/{tokenId}", (HttpContext context, string tokenId, CoordinatorOptionsStore options, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (tokenId.Length < 12 || !options.RevokeToken(tokenId)) return Results.NotFound();
    store.Audit(Owner(context), "token.revoke", "ok", tokenId[..12]); return Results.NoContent();
});

app.MapGet("/inspect/api/v1/bundles", (InspectionStore store) => Results.Json(store.ListBundles(), InspectionProtocol.Json));
app.MapPost("/inspect/api/v1/bundles", async (HttpContext context, CoordinatorOptionsStore options, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var upload = form.Files.GetFile("bundle");
    if (upload is null || upload.Length is <= 0 or > 268_435_456) return Results.BadRequest(new { error = "A bundle ZIP up to 256 MiB is required." });
    var temp = Path.Combine(options.Root, $"upload-{Guid.NewGuid():N}.zip");
    try
    {
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await upload.CopyToAsync(output, context.RequestAborted);
        var verifier = new InspectionBundleVerifier(options.Current.PublicKeys);
        var verified = verifier.VerifyAndExtract(temp, options.BundlesPath);
        var existing = store.GetBundle(verified.Manifest.BundleId);
        InspectionApiPolicy.EnsureBundleReplacementAllowed(existing, verified);
        store.SaveBundle(verified); store.Audit(Owner(context), "bundle.upload", "ok", verified.Manifest.BundleId);
        return Results.Json(new { verified.Manifest.BundleId, verified.Manifest.Version, verified.Manifest.Plan.Name }, InspectionProtocol.Json);
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException)
    {
        store.Audit(Owner(context), "bundle.upload", "denied", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
    finally { try { File.Delete(temp); } catch { } }
});

app.MapPost("/inspect/api/v1/agent-updates", async (HttpContext context, CoordinatorOptionsStore options, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var upload = form.Files.GetFile("update");
    if (upload is null || upload.Length is <= 0 or > 268_435_456) return Results.BadRequest(new { error = "A signed agent-update ZIP up to 256 MiB is required." });
    var temp = Path.Combine(options.Root, $"agent-upload-{Guid.NewGuid():N}.zip");
    try
    {
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await upload.CopyToAsync(output, context.RequestAborted);
        var verified = new InspectionAgentUpdateVerifier(options.Current.PublicKeys).VerifyAndExtract(temp, options.StagedAgentUpdatesPath);
        if (Version.Parse(verified.Manifest.Version) <= Version.Parse(InspectionProtocol.AgentVersion))
            throw new InvalidDataException("Agent update must be newer than the installed version.");
        var incoming = Path.Combine(options.IncomingAgentUpdatesPath, $"admin-{verified.PackageSha256}.zip");
        File.Copy(temp, incoming, true);
        store.Audit(Owner(context), "agent-update.upload", "ok", $"{verified.Manifest.Version}:{verified.PackageSha256}");
        return Results.Accepted(value: new { verified.Manifest.Version, verified.PackageSha256, state = "queued" });
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException)
    {
        store.Audit(Owner(context), "agent-update.upload", "denied", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
    finally { try { File.Delete(temp); } catch { } }
});

app.MapPost("/inspect/api/v1/destructive-confirmations", (HttpContext context, DestructiveConfirmationRequest request, ConfirmationManager confirmations, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var response = confirmations.Create(Owner(context), request);
    store.Audit(Owner(context), "destructive.confirm", "ok", $"{request.Action}:{request.RunId}");
    return Results.Json(response, InspectionProtocol.Json);
});
app.MapPost("/inspect/api/v1/destructive/confirm", (HttpContext context, DestructiveConfirmationRequest request, ConfirmationManager confirmations, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var response = confirmations.Create(Owner(context), request);
    store.Audit(Owner(context), "destructive.confirm", "ok", $"{request.Action}:{request.RunId}");
    return Results.Json(response, InspectionProtocol.Json);
});

app.MapPost("/inspect/api/v1/runs", (HttpContext context, RunRequest request, InspectionStore store, ConfirmationManager confirmations) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var bundle = store.GetBundle(request.BundleId);
    if (bundle is null) return Results.NotFound();
    var destructive = bundle.Manifest.Plan.Steps.Any(s => s.Type is InspectionStepType.Privileged or InspectionStepType.Fault ||
        s.Input.ValueKind == JsonValueKind.Object && s.Input.TryGetProperty("destructive", out var destructiveValue) && destructiveValue.ValueKind == JsonValueKind.True ||
        s.Type == InspectionStepType.Cleanup && s.Input.ValueKind == JsonValueKind.Object && s.Input.TryGetProperty("action", out _));
    var approved = !destructive;
    if (destructive)
    {
        if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin) || string.IsNullOrWhiteSpace(request.ConfirmationId)
            || !confirmations.Consume(request.ConfirmationId, Owner(context), "run", request.BundleId)) return Results.StatusCode(428);
        approved = true;
    }
    if (!store.TryCreateRunWhenIdle(bundle, Owner(context), approved, out var runId))
        return Results.Conflict(new { error = "이미 실행 중인 점검이 있습니다.", runId });
    store.Audit(Owner(context), "run.create", "ok", runId);
    return Results.Json(new { runId }, InspectionProtocol.Json, statusCode: 201);
});

app.MapGet("/inspect/api/v1/runs", (int? limit, InspectionStore store) =>
    Results.Json(store.ListRuns(limit ?? 50), InspectionProtocol.Json));
app.MapGet("/inspect/api/v1/runs/latest", (InspectionStore store) =>
{
    var runId = store.GetLatestRunId();
    return runId is null ? Results.NotFound() : Results.Json(store.GetRunSnapshot(runId), InspectionProtocol.Json);
});
app.MapGet("/inspect/api/v1/runs/{runId}", (string runId, InspectionStore store) =>
    store.GetRunSnapshot(runId) is { } snapshot ? Results.Json(snapshot, InspectionProtocol.Json) : Results.NotFound());
app.MapGet("/inspect/api/v1/runs/{runId}/events", (string runId, long? after, InspectionStore store) =>
    Results.Json(store.GetEvents(runId, Math.Max(0, after ?? 0)), InspectionProtocol.Json));
app.MapGet("/inspect/api/v1/runs/{runId}/artifacts", (string runId, InspectionStore store) => Results.Json(store.ListArtifacts(runId), InspectionProtocol.Json));
app.MapGet("/inspect/api/v1/runs/{runId}/report", (string runId, InspectionStore store) =>
    store.GetRunSnapshot(runId) is { } snapshot ? Results.Json(new { run=snapshot,events=store.GetEvents(runId,0,10000),artifacts=store.ListArtifacts(runId),auditChainValid=store.VerifyAuditChain() }, InspectionProtocol.Json) : Results.NotFound());
app.MapPost("/inspect/api/v1/runs/{runId}/acks/{channel}", (HttpContext context, string runId, string channel, InspectionStore store) =>
{
    if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!InspectionApiPolicy.IsSafeExternalAcknowledgementChannel(channel))return Results.BadRequest(new { error = "The acknowledgement channel is reserved or invalid." });
    var run=store.GetStoredRun(runId);if(run is null)return Results.NotFound();
    var bundle=store.GetBundle(run.BundleId);if(bundle is null)return Results.NotFound();
    if(!InspectionApiPolicy.IsMatchingRunningCheckpoint(bundle,store.GetSteps(runId),channel))return Results.Conflict(new { error = "The matching checkpoint is not currently waiting for acknowledgement." });
    store.SaveAck(runId,channel,Owner(context));store.Audit(Owner(context),"external.ack","ok",$"{runId}:{channel}");return Results.Accepted();
});
app.MapPost("/inspect/api/v1/runs/{runId}/destructive/{stepId}/approve", (HttpContext context, string runId, string stepId, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var run = store.GetStoredRun(runId);
    if (run is null) return Results.NotFound();
    var bundle = store.GetBundle(run.BundleId);
    var step = bundle?.Manifest.Plan.Steps.SingleOrDefault(candidate => candidate.Id.Equals(stepId, StringComparison.Ordinal));
    if (step is null || step.Type is not (InspectionStepType.Privileged or InspectionStepType.Fault or InspectionStepType.Cleanup)
        || step.Input.ValueKind != JsonValueKind.Object || !step.Input.TryGetProperty("action", out var actionValue)) return Results.BadRequest();
    var action = actionValue.GetString();
    if (action is not ("uninstall-app" or "nic-bounce" or "reboot")) return Results.BadRequest();
    if (!store.GetSteps(runId).TryGetValue(stepId, out var state) || state.Status != InspectionRunStatus.Running) return Results.Conflict(new { error = "The destructive step is not currently waiting for approval." });
    if (store.HasAck(runId, $"destructive-{stepId}")) return Results.Conflict(new { error = "This destructive step was already approved." });
    store.SaveAck(runId, $"destructive-{stepId}", Owner(context));
    store.Audit(Owner(context), "destructive.execute", "approved", $"{runId}:{stepId}:{action}");
    return Results.Accepted();
});
app.MapPost("/inspect/api/v1/runs/{runId}/pause", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.StatusCode(StatusCodes.Status403Forbidden);store.SetRunRequest(runId, pause: true);store.Audit(Owner(context),"run.pause","ok",runId);return Results.Accepted(); });
app.MapPost("/inspect/api/v1/runs/{runId}/resume", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.StatusCode(StatusCodes.Status403Forbidden);store.SetRunRequest(runId, pause: false);store.Audit(Owner(context),"run.resume","ok",runId);return Results.Accepted(); });
app.MapPost("/inspect/api/v1/runs/{runId}/cancel", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.StatusCode(StatusCodes.Status403Forbidden);store.SetRunRequest(runId, cancel: true);store.Audit(Owner(context),"run.cancel","ok",runId);return Results.Accepted(); });

app.MapGet("/inspect/api/v1/runs/{runId}/artifacts/{artifactId}", (string runId, string artifactId, InspectionStore store, CoordinatorOptionsStore options) =>
{
    var artifact = store.GetArtifact(runId, artifactId);
    if (artifact is null) return Results.NotFound();
    var path = Path.GetFullPath(Path.Combine(options.ArtifactsPath, artifact.Value.RelativePath));
    if (!path.StartsWith(Path.GetFullPath(options.ArtifactsPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
    return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
});

app.MapGet("/inspect/api/v1/desktop/monitors", async (SessionAgentClient session, CancellationToken token) =>
{
    try
    {
        var response = await session.SendAsync("monitors", null, token);
        return response.Ok ? Results.Json(response.Payload, InspectionProtocol.Json) : Results.Problem(response.Error, statusCode: 503);
    }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 503); }
});

app.MapPost("/inspect/api/v1/control/lease", async (HttpContext context, ControlLeaseRequest request, ControlLeaseManager leases, SessionAgentClient session, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var lease = leases.Acquire(Owner(context));
    if (lease is null) return Results.Conflict(new { error = "Another controller currently owns this room." });
    try { await session.SendAsync("reset-control", null, context.RequestAborted); } catch { }
    store.Audit(Owner(context), "control.acquire", "ok", lease.LeaseId);
    return Results.Json(lease, InspectionProtocol.Json);
});
app.MapPost("/inspect/api/v1/control/lease/{leaseId}/heartbeat", (HttpContext context, string leaseId, ControlLeaseManager leases) =>
    leases.Heartbeat(leaseId, Owner(context)) is { } lease ? Results.Json(lease, InspectionProtocol.Json) : Results.Conflict());
app.MapDelete("/inspect/api/v1/control/lease/{leaseId}", async (HttpContext context, string leaseId, ControlLeaseManager leases, SessionAgentClient session) =>
{
    var released = leases.Release(leaseId, Owner(context));
    if (released) { try { await session.SendAsync("release-input", null, context.RequestAborted); } catch { } }
    return released ? Results.NoContent() : Results.NotFound();
});

app.Map("/inspect/ws/v1/desktop", async context => await DesktopEndpoints.StreamDesktopAsync(context,
    context.RequestServices.GetRequiredService<SessionAgentClient>(), context.RequestServices.GetRequiredService<ControlLeaseManager>(),
    context.RequestServices.GetRequiredService<InspectionAuthenticator>(),
    context.RequestServices.GetRequiredService<InspectionStore>(), context.RequestAborted));
app.Map("/inspect/ws/v1/events", async context => await DesktopEndpoints.StreamEventsAsync(context,
    context.RequestServices.GetRequiredService<InspectionAuthenticator>(),
    context.RequestServices.GetRequiredService<InspectionStore>(), context.RequestAborted));

app.MapGet("/inspect/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "inspect", "index.html"));
});

app.Run();

static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

static IResult QueueStartupRerun(InspectionStore store, StartupUpdateGate startup,
    CoordinatorOptionsStore options, string owner)
{
    var bundle = store.ListStoredBundles()
        .Where(candidate => candidate.Manifest.Plan.RunAtStartup)
        .OrderByDescending(candidate => Version.TryParse(candidate.Manifest.Version, out var version) ? version : new Version(0, 0))
        .FirstOrDefault();
    if (bundle is null) return Results.NotFound(new { error = "부팅 안전 점검 패키지가 없습니다." });
    if (!InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason))
        return Results.BadRequest(new { error = reason });
    if (!store.TryCreateRunWhenIdle(bundle, owner, destructiveApproved: false, out var runId))
        return Results.Conflict(new { error = "이미 실행 중인 점검이 있습니다.", runId });
    startup.MarkInspection("inspection-queued", $"{options.Current.Room} 안전 점검을 다시 시작했습니다.", runId);
    store.Audit(owner, "startup.rerun", "queued", runId);
    return Results.Json(new { ok = true, runId, message = $"{options.Current.Room} 안전 점검을 다시 시작했습니다." },
        InspectionProtocol.Json, statusCode: StatusCodes.Status201Created);
}
