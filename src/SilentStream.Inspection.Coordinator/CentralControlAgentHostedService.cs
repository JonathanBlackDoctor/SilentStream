using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

/// <summary>
/// Keeps one outbound-only authenticated WebSocket open to the always-on control plane. The room
/// never opens an Internet-facing port, and a stopped classroom PC is represented as offline by the
/// central portal instead of making the portal itself disappear.
/// </summary>
public sealed class CentralControlAgentHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CoordinatorOptionsStore _options;
    private readonly InspectionStore _store;
    private readonly StartupUpdateGate _startup;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<CentralControlAgentHostedService> _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public CentralControlAgentHostedService(CoordinatorOptionsStore options, InspectionStore store,
        StartupUpdateGate startup, IHttpClientFactory clients, ILogger<CentralControlAgentHostedService> logger)
    {
        _options = options;
        _store = store;
        _startup = startup;
        _clients = clients;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = ReadConfiguration();
            if (config is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.SetRequestHeader("Authorization",
                    $"Device {config.RoomId}:{config.DeviceId}:{config.DeviceSecret}");
                await socket.ConnectAsync(config.WebSocketUri, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Central control connection established for {Room}.", config.RoomId);
                delay = TimeSpan.FromSeconds(2);
                await SendAsync(socket, "hello", null, new CentralAgentHello(config.RoomId,
                    config.InstallationId, Environment.MachineName, InspectionProtocol.AgentVersion,
                    DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);

                using var connected = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var receive = ReceiveLoopAsync(socket, config, connected.Token);
                while (!receive.IsCompleted && !stoppingToken.IsCancellationRequested)
                {
                    var snapshot = await CaptureSnapshotAsync(config, stoppingToken).ConfigureAwait(false);
                    await SendAsync(socket, "snapshot", null, snapshot, stoppingToken).ConfigureAwait(false);
                    await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(10), stoppingToken)).ConfigureAwait(false);
                }
                connected.Cancel();
                await IgnoreConnectionEnd(receive).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or InvalidOperationException
                                       or UriFormatException or CryptographicException or FormatException)
            {
                _logger.LogWarning("Central control connection is unavailable for {Room}: {Message}",
                    config.RoomId, ex.Message);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private AgentConfiguration? ReadConfiguration()
    {
        var current = _options.Current;
        if (!current.CentralControlEnabled || !RoomIdValid(current.RoomId)
            || string.IsNullOrWhiteSpace(current.CentralControlWebSocketUri)
            || string.IsNullOrWhiteSpace(current.CentralDeviceId)
            || string.IsNullOrWhiteSpace(current.CentralDeviceSecretMachineEnc)
            || string.IsNullOrWhiteSpace(current.CentralInstallationId)) return null;
        if (!Uri.TryCreate(current.CentralControlWebSocketUri, UriKind.Absolute, out var uri)
            || !IsSafeWebSocketUri(uri)) return null;
        return new AgentConfiguration(current.RoomId.Trim().ToLowerInvariant(), current.CentralDeviceId,
            current.CentralInstallationId, _options.UnprotectMachine(current.CentralDeviceSecretMachineEnc), uri);
    }

    private async Task<CentralAgentSnapshot> CaptureSnapshotAsync(AgentConfiguration config, CancellationToken token)
    {
        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        var client = _clients.CreateClient(nameof(CentralControlAgentHostedService));
        client.Timeout = TimeSpan.FromSeconds(5);
        var app = await GetLocalJsonAsync(client, "/api/status", appToken, token).ConfigureAwait(false);
        if (app.ValueKind == JsonValueKind.Object)
        {
            var capabilities = app.Deserialize<Dictionary<string, JsonElement>>(Json)!;
            capabilities["remoteShutdownScheduleCommands"] = JsonSerializer.SerializeToElement(true);
            app = JsonSerializer.SerializeToElement(capabilities, Json);
        }
        var audio = await GetLocalJsonAsync(client, "/api/audio", appToken, token).ConfigureAwait(false);
        if (audio.ValueKind == JsonValueKind.Object)
        {
            var capabilities = audio.Deserialize<Dictionary<string, JsonElement>>(Json)!;
            capabilities["remoteMicrophoneCommands"] = JsonSerializer.SerializeToElement(true);
            audio = JsonSerializer.SerializeToElement(capabilities, Json);
        }
        var recovery = await GetLocalJsonAsync(client, "/api/recovery/status", appToken, token).ConfigureAwait(false);
        var playlists = await GetLocalJsonAsync(client, "/api/inspection/playlists?knownOnly=true", appToken,
            token, inspectionBridge: true).ConfigureAwait(false);
        var latest = _store.GetLatestRunId();
        var inspection = JsonSerializer.SerializeToElement(new
        {
            startup = _startup.Snapshot,
            run = latest is null ? null : _store.GetRunSnapshot(latest),
            auditChainValid = _store.VerifyAuditChain()
        }, InspectionProtocol.Json);
        return new CentralAgentSnapshot(config.RoomId, config.InstallationId, Environment.MachineName,
            InspectionProtocol.AgentVersion, DateTimeOffset.UtcNow, app, audio, inspection, recovery, playlists);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, AgentConfiguration config, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var bytes = await ReceiveMessageAsync(socket, token).ConfigureAwait(false);
            if (bytes is null) return;
            CentralAgentEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<CentralAgentEnvelope>(bytes, Json); }
            catch (JsonException) { continue; }
            if (envelope is null || envelope.SchemaVersion != CentralControlProtocol.SchemaVersion
                || envelope.Type != "command") continue;
            var dispatch = envelope.Payload.Deserialize<CentralCommandDispatch>(Json);
            if (dispatch is null || dispatch.CommandId != envelope.RequestId) continue;
            var result = await ExecuteCommandAsync(dispatch, token).ConfigureAwait(false);
            await SendAsync(socket, "commandResult", dispatch.CommandId, result, token).ConfigureAwait(false);
        }
    }

    private async Task<CentralCommandResult> ExecuteCommandAsync(CentralCommandDispatch command, CancellationToken token)
    {
        if (!TryExpectedTarget(command.Action, out var expectedMethod, out var expectedPath))
            return Failure(command.CommandId, HttpStatusCode.BadRequest, "지원하지 않는 중앙 명령입니다.");
        if (command.Payload.ValueKind != JsonValueKind.Object
            || !command.Payload.TryGetProperty("method", out var methodValue)
            || !command.Payload.TryGetProperty("path", out var pathValue)
            || !string.Equals(methodValue.GetString(), expectedMethod, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pathValue.GetString(), expectedPath, StringComparison.Ordinal))
            return Failure(command.CommandId, HttpStatusCode.BadRequest, "중앙 명령 대상이 안전 정책과 일치하지 않습니다.");

        var appToken = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
        if (command.Action == "system.shutdown")
            return await ExecuteSystemShutdownAsync(command.CommandId, appToken, token).ConfigureAwait(false);
        var inspection = expectedPath.StartsWith("/inspect/", StringComparison.Ordinal);
        var uri = inspection ? "http://127.0.0.1:8790" + expectedPath : "http://127.0.0.1:8787" + expectedPath;
        using var request = new HttpRequestMessage(new HttpMethod(expectedMethod), uri);
        if (inspection) request.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", appToken);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
        if (command.Payload.TryGetProperty("body", out var body) && body.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(command.Action == "system.shutdown.schedule.cancel" ? 80 : 15));
            using var response = await _clients.CreateClient(nameof(CentralControlAgentHostedService))
                .SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];
            return new CentralCommandResult(command.CommandId, response.IsSuccessStatusCode, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json", text,
                response.IsSuccessStatusCode ? null : $"로컬 서비스가 HTTP {(int)response.StatusCode}를 반환했습니다.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Failure(command.CommandId, HttpStatusCode.ServiceUnavailable, ex.Message);
        }
    }

    private async Task<CentralCommandResult> ExecuteSystemShutdownAsync(string commandId, string appToken,
        CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(55));
            var client = _clients.CreateClient(nameof(CentralControlAgentHostedService));
            using var prepare = new HttpRequestMessage(HttpMethod.Post,
                "http://127.0.0.1:8787/api/system/shutdown/prepare");
            prepare.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
            prepare.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var preparedResponse = await client.SendAsync(prepare, timeout.Token).ConfigureAwait(false);
            var preparedText = await preparedResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!preparedResponse.IsSuccessStatusCode)
                return new CentralCommandResult(commandId, false, (int)preparedResponse.StatusCode,
                    preparedResponse.Content.Headers.ContentType?.ToString() ?? "application/json", preparedText,
                    "부팅 안전 점검이 중앙 종료 요청을 차단했습니다.", DateTimeOffset.UtcNow);

            using var document = JsonDocument.Parse(preparedText);
            var root = document.RootElement;
            var confirmationId = root.GetProperty("confirmationId").GetString();
            var notBefore = root.GetProperty("notBeforeUtc").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(confirmationId))
                return Failure(commandId, HttpStatusCode.Conflict, "종료 확인 번호가 발급되지 않았습니다.");
            var delay = notBefore - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay.Add(TimeSpan.FromMilliseconds(100)), timeout.Token)
                .ConfigureAwait(false);

            using var confirm = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8787/api/system/shutdown");
            confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
            confirm.Content = new StringContent(JsonSerializer.Serialize(new { confirmationId }, Json),
                Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(confirm, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];
            return new CentralCommandResult(commandId, response.IsSuccessStatusCode, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json", text,
                response.IsSuccessStatusCode ? null : $"로컬 서비스가 HTTP {(int)response.StatusCode}를 반환했습니다.",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   or InvalidOperationException)
        {
            return Failure(commandId, HttpStatusCode.ServiceUnavailable, ex.Message);
        }
    }

    private async Task<JsonElement> GetLocalJsonAsync(HttpClient client, string path, string token,
        CancellationToken cancellationToken, bool inspectionBridge = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8787" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (inspectionBridge)
                request.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return JsonSerializer.SerializeToElement(new { available = false, statusCode = (int)response.StatusCode });
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return JsonSerializer.SerializeToElement(new { available = false, error = ex.Message });
        }
    }

    private async Task SendAsync<T>(ClientWebSocket socket, string type, string? requestId, T payload,
        CancellationToken cancellationToken)
    {
        var envelope = new CentralAgentEnvelope(CentralControlProtocol.SchemaVersion, type, requestId,
            JsonSerializer.SerializeToElement(payload, Json));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || output.Length + result.Count > 1024 * 1024) return null;
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return output.ToArray();
        }
    }

    private static bool TryExpectedTarget(string action, out string method, out string path)
    {
        (method, path) = action switch
        {
            "live.start" => ("POST", "/api/live/start"),
            "live.retry" => ("POST", "/api/live/retry"),
            "audio.select" => ("POST", "/api/audio/select"),
            "audio.refresh" => ("POST", "/api/audio/refresh"),
            "audio.restore" => ("POST", "/api/audio/restore"),
            "live.stopBroadcast" => ("POST", "/api/live/stop-broadcast"),
            "live.stopAll" => ("POST", "/api/live/stop"),
            "app.restart" => ("POST", "/api/app/restart"),
            "inspection.rerun" => ("POST", "/inspect/internal/v1/startup/rerun"),
            "system.shutdown" => ("POST", "/api/system/shutdown"),
            "system.shutdown.schedule" => ("POST", "/api/system/shutdown/schedule"),
            "system.shutdown.schedule.cancel" => ("POST", "/api/system/shutdown/schedule/cancel"),
            _ => (string.Empty, string.Empty)
        };
        return method.Length > 0;
    }

    private static bool IsSafeWebSocketUri(Uri uri) => uri.Scheme == "wss"
        || uri.Scheme == "ws" && IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)
        || uri.Scheme == "ws" && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool RoomIdValid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static CentralCommandResult Failure(string id, HttpStatusCode status, string error) =>
        new(id, false, (int)status, "application/json", string.Empty, error, DateTimeOffset.UtcNow);

    private static async Task IgnoreConnectionEnd(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private sealed record AgentConfiguration(string RoomId, string DeviceId, string InstallationId,
        string DeviceSecret, Uri WebSocketUri);
}
