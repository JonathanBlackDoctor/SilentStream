using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;

var optionsStore = new CoordinatorOptionsStore();
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(service => service.ServiceName = "SilentStream Inspection Coordinator");
builder.WebHost.UseUrls($"http://{optionsStore.Current.BindAddress}:{optionsStore.Current.Port}");
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 270L * 1024 * 1024);
builder.Services.AddSingleton(optionsStore);
builder.Services.AddSingleton<InspectionStore>();
builder.Services.AddSingleton<InspectionAuthenticator>();
builder.Services.AddSingleton<ControlLeaseManager>();
builder.Services.AddSingleton<ConfirmationManager>();
builder.Services.AddSingleton<SessionAgentClient>();
builder.Services.AddSingleton<BrokerClient>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<PreseedBundleHostedService>();
builder.Services.AddHostedService<AgentUpdateHostedService>();
builder.Services.AddHostedService<InspectionEngine>();
builder.Services.AddHostedService<CloudflaredHostedService>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrWhiteSpace(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        || !string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    var path = context.Request.Path;
    var protectedPath = path.StartsWithSegments("/inspect/api") || path.StartsWithSegments("/inspect/ws");
    var publicPath = path == "/inspect/api/v1/pair" || path == "/inspect/api/v1/health";
    if (!protectedPath || publicPath) { await next(); return; }
    var auth = context.RequestServices.GetRequiredService<InspectionAuthenticator>();
    var principal = auth.Authenticate(context);
    if (principal is null) { context.Response.StatusCode = 401; return; }
    context.User = principal;
    await next();
});

app.MapGet("/inspect/api/v1/health", (CoordinatorOptionsStore options) => Results.Json(new
{
    ok = true, version = InspectionProtocol.AgentVersion, room = options.Current.Room,
    session = Environment.UserInteractive, timestampUtc = DateTimeOffset.UtcNow
}, InspectionProtocol.Json));

app.MapPost("/inspect/api/v1/pair", (HttpContext context, PairRequest request, InspectionAuthenticator auth, InspectionStore store) =>
{
    var forwarded = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
    var remote = System.Net.IPAddress.TryParse(forwarded, out var forwardedAddress)
        ? forwardedAddress.ToString() : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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

app.MapGet("/inspect/api/v1/tokens", (HttpContext context, CoordinatorOptionsStore options) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
    return Results.Json(options.Current.Tokens.Select(t => new { tokenId=t.Hash[..Math.Min(12,t.Hash.Length)],role=t.Role.ToString().ToLowerInvariant(),t.Label,t.CreatedAtUtc,t.ExpiresAtUtc,t.RevokedAtUtc,t.LastUsedAtUtc }), InspectionProtocol.Json);
});
app.MapDelete("/inspect/api/v1/tokens/{tokenId}", (HttpContext context, string tokenId, CoordinatorOptionsStore options, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
    if (tokenId.Length < 12 || !options.RevokeToken(tokenId)) return Results.NotFound();
    store.Audit(Owner(context), "token.revoke", "ok", tokenId[..12]); return Results.NoContent();
});

app.MapGet("/inspect/api/v1/bundles", (InspectionStore store) => Results.Json(store.ListBundles(), InspectionProtocol.Json));
app.MapPost("/inspect/api/v1/bundles", async (HttpContext context, CoordinatorOptionsStore options, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
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
        if (existing is not null && Version.Parse(verified.Manifest.Version) < Version.Parse(existing.Manifest.Version))
            throw new InvalidDataException($"Bundle downgrade is not allowed ({existing.Manifest.Version} -> {verified.Manifest.Version}).");
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
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
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
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
    var response = confirmations.Create(Owner(context), request);
    store.Audit(Owner(context), "destructive.confirm", "ok", $"{request.Action}:{request.RunId}");
    return Results.Json(response, InspectionProtocol.Json);
});
app.MapPost("/inspect/api/v1/destructive/confirm", (HttpContext context, DestructiveConfirmationRequest request, ConfirmationManager confirmations, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
    var response = confirmations.Create(Owner(context), request);
    store.Audit(Owner(context), "destructive.confirm", "ok", $"{request.Action}:{request.RunId}");
    return Results.Json(response, InspectionProtocol.Json);
});

app.MapPost("/inspect/api/v1/runs", (HttpContext context, RunRequest request, InspectionStore store, ConfirmationManager confirmations) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator)) return Results.Forbid();
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
    var runId = store.CreateRun(bundle, Owner(context), approved);
    store.Audit(Owner(context), "run.create", "ok", runId);
    return Results.Json(new { runId }, InspectionProtocol.Json, statusCode: 201);
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
    if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.Forbid();
    if(channel.Length is < 1 or > 40 || channel.Any(c=>!char.IsLetterOrDigit(c)&&c is not '-' and not '_'))return Results.BadRequest();
    if(store.GetStoredRun(runId) is null)return Results.NotFound();store.SaveAck(runId,channel,Owner(context));store.Audit(Owner(context),"external.ack","ok",$"{runId}:{channel}");return Results.Accepted();
});
app.MapPost("/inspect/api/v1/runs/{runId}/destructive/{stepId}/approve", (HttpContext context, string runId, string stepId, InspectionStore store) =>
{
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Admin)) return Results.Forbid();
    var run = store.GetStoredRun(runId);
    if (run is null) return Results.NotFound();
    var bundle = store.GetBundle(run.BundleId);
    var step = bundle?.Manifest.Plan.Steps.SingleOrDefault(candidate => candidate.Id.Equals(stepId, StringComparison.Ordinal));
    if (step is null || step.Input.ValueKind != JsonValueKind.Object || !step.Input.TryGetProperty("action", out var actionValue)) return Results.BadRequest();
    var action = actionValue.GetString();
    if (action is not ("uninstall-app" or "nic-bounce" or "reboot")) return Results.BadRequest();
    if (!store.GetSteps(runId).TryGetValue(stepId, out var state) || state.Status != InspectionRunStatus.Running) return Results.Conflict(new { error = "The destructive step is not currently waiting for approval." });
    if (store.HasAck(runId, $"destructive-{stepId}")) return Results.Conflict(new { error = "This destructive step was already approved." });
    store.SaveAck(runId, $"destructive-{stepId}", Owner(context));
    store.Audit(Owner(context), "destructive.execute", "approved", $"{runId}:{stepId}:{action}");
    return Results.Accepted();
});
app.MapPost("/inspect/api/v1/runs/{runId}/pause", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.Forbid();store.SetRunRequest(runId, pause: true);store.Audit(Owner(context),"run.pause","ok",runId);return Results.Accepted(); });
app.MapPost("/inspect/api/v1/runs/{runId}/resume", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.Forbid();store.SetRunRequest(runId, pause: false);store.Audit(Owner(context),"run.resume","ok",runId);return Results.Accepted(); });
app.MapPost("/inspect/api/v1/runs/{runId}/cancel", (HttpContext context, string runId, InspectionStore store) => { if(!InspectionAuthenticator.HasRole(context,InspectionRole.Operator))return Results.Forbid();store.SetRunRequest(runId, cancel: true);store.Audit(Owner(context),"run.cancel","ok",runId);return Results.Accepted(); });

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
    if (!InspectionAuthenticator.HasRole(context, InspectionRole.Operator)) return Results.Forbid();
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
