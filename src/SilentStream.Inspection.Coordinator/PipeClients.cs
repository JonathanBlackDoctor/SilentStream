using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public interface ISessionAgentClient
{
    Task<SessionResponse> SendAsync(string action, object? payload, CancellationToken cancellationToken);
}

public sealed class SessionAgentClient : ISessionAgentClient
{
    public async Task<SessionResponse> SendAsync(string action, object? payload, CancellationToken cancellationToken)
    {
        var request = new SessionRequest(Guid.NewGuid().ToString("N"), action,
            payload is null ? null : JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json));
        using var pipe = new NamedPipeClientStream(".", InspectionProtocol.SessionPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        await WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SessionResponse>(pipe, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, InspectionProtocol.Json);
        if (data.Length > 16 * 1024 * 1024) throw new InvalidDataException("Pipe message is too large.");
        var length = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid pipe frame length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data, InspectionProtocol.Json) ?? throw new InvalidDataException("Pipe response is empty.");
    }
}

public interface IBrokerClient
{
    Task<BrokerResponse> SendAsync(string action, object? payload, string confirmationId,
        CancellationToken cancellationToken);
}

public sealed class BrokerClient : IBrokerClient
{
    private readonly CoordinatorOptionsStore _options;
    public BrokerClient(CoordinatorOptionsStore options) => _options = options;

    public async Task<BrokerResponse> SendAsync(string action, object? payload, string confirmationId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var requestedAtUtc = DateTimeOffset.UtcNow;
        var element = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json);
        var auth = Sign(id, action, confirmationId, requestedAtUtc, element);
        var request = new BrokerRequest(id, action, element, confirmationId, requestedAtUtc, auth);
        using var pipe = new NamedPipeClientStream(".", InspectionProtocol.BrokerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
        await SessionAgentClient.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await SessionAgentClient.ReadAsync<BrokerResponse>(pipe, cancellationToken).ConfigureAwait(false);
    }

    private string Sign(string id, string action, string confirmationId, DateTimeOffset requestedAtUtc, JsonElement? payload)
    {
        var canonicalPayload = payload is null ? string.Empty : JsonSerializer.Serialize(payload.Value, InspectionProtocol.Json);
        var body = $"{id}\n{action}\n{confirmationId}\n{requestedAtUtc:O}\n{canonicalPayload}";
        var key = Encoding.UTF8.GetBytes(_options.UnprotectMachine(_options.Current.BrokerSecretMachineEnc));
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
    }
}
