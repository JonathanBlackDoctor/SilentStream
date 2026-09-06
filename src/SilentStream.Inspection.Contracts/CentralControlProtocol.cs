using System.Text.Json;

namespace SilentStream.Inspection;

/// <summary>
/// Versioned messages exchanged between the always-on control plane and a classroom Coordinator.
/// The classroom always initiates the TLS/WebSocket connection; no inbound classroom port is used.
/// </summary>
public static class CentralControlProtocol
{
    public const int SchemaVersion = 1;
}

public sealed record CentralAgentEnvelope(
    int SchemaVersion,
    string Type,
    string? RequestId,
    JsonElement Payload);

public sealed record CentralAgentHello(
    string RoomId,
    string InstallationId,
    string MachineName,
    string AgentVersion,
    DateTimeOffset ConnectedAtUtc);

public sealed record CentralAgentSnapshot(
    string RoomId,
    string InstallationId,
    string MachineName,
    string AgentVersion,
    DateTimeOffset CapturedAtUtc,
    JsonElement App,
    JsonElement Audio,
    JsonElement Inspection,
    JsonElement Recovery,
    JsonElement Playlists);

public sealed record CentralCommandRequest(
    string Action,
    JsonElement Payload);

public sealed record CentralCommandDispatch(
    string CommandId,
    string Action,
    JsonElement Payload,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc);

public sealed record CentralCommandResult(
    string CommandId,
    bool Ok,
    int StatusCode,
    string ContentType,
    string Body,
    string? Error,
    DateTimeOffset CompletedAtUtc);

public sealed record CentralEnrollmentRedeemRequest(
    string RoomId,
    string InstallationId,
    string MachineName,
    string EnrollmentCode);

public sealed record CentralEnrollmentResponse(
    string RoomId,
    string DeviceId,
    string InstallationId,
    string DeviceSecret,
    string ControlPlaneBaseUri,
    string DeviceWebSocketUri,
    string PackageDownloadUri,
    string PackageSha256,
    long PackageLength,
    DateTimeOffset RegisteredAtUtc);
