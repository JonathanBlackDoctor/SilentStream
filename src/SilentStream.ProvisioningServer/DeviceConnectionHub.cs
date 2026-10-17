using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SilentStream.Inspection;

public sealed class DeviceConnectionHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CentralCommandResult>> _pending = new(StringComparer.Ordinal);
    private readonly ControlPlaneStore _store;
    private readonly ControlPlaneOptions _options;
    private readonly ILogger<DeviceConnectionHub> _logger;

    public DeviceConnectionHub(ControlPlaneStore store, IOptions<ControlPlaneOptions> options,
        ILogger<DeviceConnectionHub> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConnected(string roomId) => _sessions.TryGetValue(RoomId.Normalize(roomId), out var session)
        && session.Socket.State == WebSocketState.Open;

    public bool IsConnected(string roomId, string deviceId) =>
        _sessions.TryGetValue(RoomId.Normalize(roomId), out var session)
        && session.Identity.DeviceId == deviceId && session.Socket.State == WebSocketState.Open;

    public async Task<CentralCommandResult> IssueAsync(string roomId, string action, JsonElement payload,
        string requestedBy, CancellationToken cancellationToken)
    {
        roomId = RoomId.Normalize(roomId);
        var commandId = _store.AddCommand(roomId, action, payload, requestedBy);
        if (!_sessions.TryGetValue(roomId, out var session) || session.Socket.State != WebSocketState.Open)
        {
            const string offline = "강의실 PC가 오프라인입니다. 명령은 실행되지 않았습니다.";
            _store.FailCommand(commandId, offline);
            return new CentralCommandResult(commandId, false, StatusCodes.Status503ServiceUnavailable,
                "application/json", string.Empty, offline, DateTimeOffset.UtcNow);
        }

        var completion = new TaskCompletionSource<CentralCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(commandId, completion)) throw new InvalidOperationException("Duplicate command id.");
        try
        {
            var dispatch = new CentralCommandDispatch(commandId, action, payload.Clone(), requestedBy, DateTimeOffset.UtcNow);
            await session.Outgoing.Writer.WriteAsync(dispatch, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(action == "system.shutdown.schedule.cancel"
                ? 90 : Math.Clamp(_options.CommandTimeoutSeconds, 5, 120)));
            var result = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            _store.CompleteCommand(commandId, result);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string message = "강의실 PC의 응답 시간이 초과되었습니다.";
            _store.FailCommand(commandId, message);
            return new CentralCommandResult(commandId, false, StatusCodes.Status504GatewayTimeout,
                "application/json", string.Empty, message, DateTimeOffset.UtcNow);
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    public async Task AcceptAsync(DeviceIdentity identity, WebSocket socket, CancellationToken cancellationToken)
    {
        var room = RoomId.Normalize(identity.RoomId);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new DeviceSession(identity, socket, lifetime);
        if (_sessions.TryGetValue(room, out var previous)) previous.Lifetime.Cancel();
        _sessions[room] = session;
        _store.MarkConnected(identity, true);
        _logger.LogInformation("Central agent connected for room {Room} from {Machine}.", room, identity.MachineName);

        try
        {
            var send = SendLoopAsync(session, lifetime.Token);
            var receive = ReceiveLoopAsync(session, lifetime.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            lifetime.Cancel();
            await Task.WhenAll(IgnoreCancellation(send), IgnoreCancellation(receive)).ConfigureAwait(false);
        }
        finally
        {
            if (_sessions.TryGetValue(room, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(room, out _);
                _store.MarkConnected(identity, false);
            }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            _logger.LogInformation("Central agent disconnected for room {Room}.", room);
        }
    }

    private async Task SendLoopAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        await foreach (var command in session.Outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = new CentralAgentEnvelope(CentralControlProtocol.SchemaVersion, "command", command.CommandId,
                JsonSerializer.SerializeToElement(command, Json));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
        {
            var bytes = await ReceiveMessageAsync(session.Socket, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return;
            CentralAgentEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<CentralAgentEnvelope>(bytes, Json); }
            catch (JsonException) { continue; }
            if (envelope is null || envelope.SchemaVersion != CentralControlProtocol.SchemaVersion) continue;

            try
            {
                if (envelope.Type == "snapshot")
                {
                    var snapshot = envelope.Payload.Deserialize<CentralAgentSnapshot>(Json);
                    if (snapshot is not null && RoomId.Normalize(snapshot.RoomId) == session.Identity.RoomId
                        && snapshot.InstallationId == session.Identity.InstallationId)
                        _store.SaveSnapshot(session.Identity, snapshot);
                }
                else if (envelope.Type == "commandResult")
                {
                    var result = envelope.Payload.Deserialize<CentralCommandResult>(Json);
                    if (result is not null && _pending.TryGetValue(result.CommandId, out var completion))
                        completion.TrySetResult(result);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Ignored malformed central agent message for {Room}: {Message}",
                    session.Identity.RoomId, ex.Message);
            }
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) return null;
            if (output.Length + result.Count > 4 * 1024 * 1024) return null;
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return output.ToArray();
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ChannelClosedException) { }
    }

    private sealed class DeviceSession : IDisposable
    {
        public DeviceSession(DeviceIdentity identity, WebSocket socket, CancellationTokenSource lifetime)
        {
            Identity = identity;
            Socket = socket;
            Lifetime = lifetime;
        }

        public DeviceIdentity Identity { get; }
        public WebSocket Socket { get; }
        public CancellationTokenSource Lifetime { get; }
        public Channel<CentralCommandDispatch> Outgoing { get; } = Channel.CreateBounded<CentralCommandDispatch>(
            new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        public void Dispose() => Lifetime.Dispose();
    }
}

public static class CentralCommandPolicy
{
    private static readonly IReadOnlyDictionary<string, CommandTarget> Targets =
        new Dictionary<string, CommandTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["live.start"] = new("POST", "/api/live/start", ControlPlaneRole.Manager),
            ["live.retry"] = new("POST", "/api/live/retry", ControlPlaneRole.Manager),
            ["live.stopBroadcast"] = new("POST", "/api/live/stop-broadcast", ControlPlaneRole.Manager),
            ["live.stopAll"] = new("POST", "/api/live/stop", ControlPlaneRole.Admin),
            ["app.restart"] = new("POST", "/api/app/restart", ControlPlaneRole.Manager),
            ["inspection.rerun"] = new("POST", "/inspect/internal/v1/startup/rerun", ControlPlaneRole.Manager),
            ["system.shutdown"] = new("POST", "/api/system/shutdown", ControlPlaneRole.Admin),
            ["system.shutdown.schedule"] = new("POST", "/api/system/shutdown/schedule", ControlPlaneRole.Admin),
            ["system.shutdown.schedule.cancel"] = new("POST", "/api/system/shutdown/schedule/cancel", ControlPlaneRole.Admin)
        };

    public static bool TryResolve(string? action, out CommandTarget target) =>
        Targets.TryGetValue(action ?? string.Empty, out target!);

    public static IReadOnlyCollection<string> Actions => Targets.Keys.ToArray();
}

public sealed record CommandTarget(string Method, string Path, ControlPlaneRole MinimumRole);
