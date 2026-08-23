using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Diagnostics;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public static class DesktopEndpoints
{
    public static async Task StreamDesktopAsync(HttpContext context, SessionAgentClient session, ControlLeaseManager leases, InspectionAuthenticator auth, InspectionStore store, CancellationToken token)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var control = string.Equals(context.Request.Query["mode"], "control", StringComparison.OrdinalIgnoreCase);
        if (control && !InspectionAuthenticator.HasRole(context, InspectionRole.Operator)) { context.Response.StatusCode = 403; return; }
        var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var leaseId = context.Request.Query["lease"].ToString();
        if (control && !leases.Validate(leaseId, owner)) { context.Response.StatusCode = 409; return; }
        var monitor = int.TryParse(context.Request.Query["monitor"], out var selected) ? Math.Clamp(selected, 0, 31) : 0;
        var protocol = context.WebSockets.WebSocketRequestedProtocols.Contains("silentstream") ? "silentstream" : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, context.RequestAborted);
        long frameId = 0;
        var receive = ReceiveInputsAsync(socket, session, leases, store, owner, leaseId, control, () => Interlocked.Read(ref frameId), linked.Token);
        try
        {
            var maxWidth = control ? 1280 : 480;
            var quality = control ? 70 : 55;
            var targetFps = control ? 12 : 2;
            var goodFrames = 0;
            var metricsAt = Stopwatch.StartNew();
            var metricsFrames = 0;
            while (!linked.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var frameClock = Stopwatch.StartNew();
                if (!auth.IsActive(context.User) || control && !leases.Validate(leaseId, owner)) break;
                var response = await session.SendAsync("capture", new { monitor, maxWidth, quality }, linked.Token).ConfigureAwait(false);
                if (!response.Ok || response.Payload is null) throw new InvalidOperationException(response.Error ?? "Session agent capture failed.");
                if (control && response.Payload.Value.TryGetProperty("controlDisabled", out var disabled) && disabled.GetBoolean())
                {
                    store.Audit(owner, "control.emergency-release", "ok", leaseId);
                    leases.Release(leaseId, owner);
                    break;
                }
                var jpeg = Convert.FromBase64String(response.Payload.Value.GetProperty("jpegBase64").GetString()!);
                var packet = new byte[jpeg.Length + 8];
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), Interlocked.Increment(ref frameId));
                jpeg.CopyTo(packet, 8);
                await socket.SendAsync(packet, WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
                metricsFrames++;
                var slow = frameClock.Elapsed > TimeSpan.FromMilliseconds(control ? 180 : 650) || jpeg.Length > (control ? 500_000 : 180_000);
                if (slow)
                {
                    goodFrames = 0;
                    maxWidth = Math.Max(control ? 640 : 360, maxWidth - 160);
                    quality = Math.Max(40, quality - 5);
                    targetFps = Math.Max(control ? 6 : 1, targetFps - 1);
                }
                else if (++goodFrames >= 30)
                {
                    goodFrames = 0;
                    maxWidth = Math.Min(control ? 1280 : 480, maxWidth + 160);
                    quality = Math.Min(control ? 70 : 55, quality + 3);
                    targetFps = Math.Min(control ? 12 : 2, targetFps + 1);
                }
                if (metricsAt.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    store.AppendEvent(null, "desktop.telemetry", new { owner, control, frames = metricsFrames, seconds = metricsAt.Elapsed.TotalSeconds, maxWidth, quality, targetFps });
                    metricsAt.Restart(); metricsFrames = 0;
                }
                var remaining = TimeSpan.FromSeconds(1d / targetFps) - frameClock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            store.AppendEvent(null, "desktop.error", new { error = ex.Message });
        }
        finally
        {
            linked.Cancel();
            try { await receive.ConfigureAwait(false); } catch { }
            if (control) leases.Release(leaseId, owner);
            try { await session.SendAsync("release-input", null, CancellationToken.None).ConfigureAwait(false); } catch { }
            if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task StreamEventsAsync(HttpContext context, InspectionAuthenticator auth, InspectionStore store, CancellationToken token)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var protocol = context.WebSockets.WebSocketRequestedProtocols.Contains("silentstream") ? "silentstream" : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol).ConfigureAwait(false);
        long after = long.TryParse(context.Request.Query["after"], out var value) ? Math.Max(0, value) : 0;
        var runId = string.IsNullOrWhiteSpace(context.Request.Query["runId"]) ? null : context.Request.Query["runId"].ToString();
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open && auth.IsActive(context.User))
        {
            var events = store.GetEvents(runId, after, 200);
            foreach (var item in events)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(item, InspectionProtocol.Json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(bytes);
                after = json.RootElement.GetProperty("sequence").GetInt64();
            }
            await Task.Delay(events.Count == 0 ? 1000 : 50, token).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveInputsAsync(WebSocket socket, SessionAgentClient session, ControlLeaseManager leases, InspectionStore store,
        string owner, string leaseId, bool control, Func<long> latestFrame, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (!control || result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage || !leases.Validate(leaseId, owner)) continue;
            var input = JsonSerializer.Deserialize<DesktopInput>(buffer.AsSpan(0, result.Count), InspectionProtocol.Json);
            if (input is null || input.LeaseId != leaseId) continue;
            var newest = latestFrame();
            if (input.FrameId <= 0 || newest - input.FrameId > 5)
            {
                store.Audit(owner, "control.input", "rejected", $"stale:{input.Kind}:frame={input.FrameId}:latest={newest}");
                continue;
            }
            var response = await session.SendAsync("input", input, token).ConfigureAwait(false);
            store.Audit(owner, "control.input", response.Ok ? "ok" : "rejected",
                $"{input.Kind}:frame={input.FrameId}:monitor={input.Monitor}{(response.Ok ? string.Empty : $":{response.Error}")}");
            if (!response.Ok) { leases.Release(leaseId, owner); return; }
        }
    }
}
