using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SilentStream.Inspection;

public sealed class ControlPlaneStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private ControlPlaneState _state;
    private PackageIdentity _packageCache = new(string.Empty, 0, string.Empty);
    private DateTime _packageCacheLastWriteUtc;

    public ControlPlaneStore(IOptions<ControlPlaneOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.StateFile;
        _path = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _state = Load();
        // WebSocket presence is process-local. Never resurrect an "online" flag from a prior
        // server process after a restart; the agent will mark itself connected again.
        foreach (var device in _state.Devices) device.Connected = false;
    }

    public string CreateEnrollment(string roomId, string createdBy, TimeSpan lifetime)
    {
        roomId = RoomId.Normalize(roomId);
        if (!RoomId.IsValid(roomId)) throw new ArgumentException("Invalid room id.", nameof(roomId));
        var code = Secret();
        lock (_gate)
        {
            RemoveExpiredLocked(DateTimeOffset.UtcNow);
            _state.Enrollments.Add(new EnrollmentTicket
            {
                Id = Guid.NewGuid().ToString("N"), RoomId = roomId, CodeHash = Hash(code), CreatedBy = createdBy,
                CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.Add(lifetime)
            });
            SaveLocked();
        }
        return code;
    }

    public CentralEnrollmentResponse? Redeem(CentralEnrollmentRedeemRequest request, ControlPlaneOptions options)
    {
        var room = RoomId.Normalize(request.RoomId);
        if (!RoomId.IsValid(room) || string.IsNullOrWhiteSpace(request.InstallationId)
            || request.InstallationId.Length > 128 || string.IsNullOrWhiteSpace(request.MachineName)
            || request.MachineName.Length > 128 || string.IsNullOrWhiteSpace(request.EnrollmentCode)) return null;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            RemoveExpiredLocked(now);
            var suppliedHash = Hash(request.EnrollmentCode);
            var ticket = _state.Enrollments.FirstOrDefault(candidate => candidate.RoomId == room
                && candidate.UsedAtUtc is null && candidate.ExpiresAtUtc > now
                && FixedEquals(candidate.CodeHash, suppliedHash));
            if (ticket is null) return null;

            ticket.UsedAtUtc = now;
            var secret = Secret();
            var deviceId = Guid.NewGuid().ToString("N");
            _state.Devices.Add(new RegisteredDevice
            {
                DeviceId = deviceId, RoomId = room, InstallationId = request.InstallationId.Trim(),
                MachineName = request.MachineName.Trim(), SecretHash = Hash(secret), RegisteredBy = ticket.CreatedBy,
                RegisteredAtUtc = now
            });
            SaveLocked();

            var baseUri = options.PublicBaseUri.TrimEnd('/');
            var deviceBaseUri = (string.IsNullOrWhiteSpace(options.DeviceBaseUri)
                ? options.PublicBaseUri : options.DeviceBaseUri).TrimEnd('/');
            var wsBase = deviceBaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "wss://" + deviceBaseUri["https://".Length..]
                : deviceBaseUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "ws://" + deviceBaseUri["http://".Length..]
                    : deviceBaseUri;
            var package = ResolvePackage(options.PreparedPackagePath);
            return new CentralEnrollmentResponse(room, deviceId, request.InstallationId.Trim(), secret, baseUri,
                $"{wsBase}/api/device/connect", $"{deviceBaseUri}/api/device/package", package.Hash, package.Length, now);
        }
    }

    public DeviceIdentity? Authenticate(string roomId, string deviceId, string deviceSecret)
    {
        var room = RoomId.Normalize(roomId);
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceSecret)) return null;
        var hash = Hash(deviceSecret);
        lock (_gate)
        {
            var device = _state.Devices.FirstOrDefault(candidate => candidate.RoomId == room
                && candidate.DeviceId == deviceId && FixedEquals(candidate.SecretHash, hash)
                && candidate.RevokedAtUtc is null);
            return device is null ? null : new DeviceIdentity(device.RoomId, device.DeviceId,
                device.InstallationId, device.MachineName);
        }
    }

    public void SaveSnapshot(DeviceIdentity identity, CentralAgentSnapshot snapshot)
    {
        lock (_gate)
        {
            var device = FindLocked(identity);
            if (device is null) return;
            device.MachineName = snapshot.MachineName;
            device.AgentVersion = snapshot.AgentVersion;
            device.LastSeenAtUtc = snapshot.CapturedAtUtc;
            device.App = snapshot.App.Clone();
            device.Audio = snapshot.Audio.Clone();
            device.Inspection = snapshot.Inspection.Clone();
            device.Recovery = snapshot.Recovery.Clone();
            if (DateTimeOffset.UtcNow - device.LastPersistedAtUtc >= TimeSpan.FromSeconds(30))
            {
                device.LastPersistedAtUtc = DateTimeOffset.UtcNow;
                SaveLocked();
            }
        }
    }

    public void MarkConnected(DeviceIdentity identity, bool connected)
    {
        lock (_gate)
        {
            var device = FindLocked(identity);
            if (device is null) return;
            device.Connected = connected;
            device.LastSeenAtUtc = DateTimeOffset.UtcNow;
            if (connected)
            {
                // Enrollment is intentionally non-destructive. Only retire the previous installation
                // after the replacement has proved that it can authenticate and reach the control plane.
                foreach (var previous in _state.Devices.Where(candidate =>
                             candidate.RoomId == identity.RoomId && candidate.DeviceId != identity.DeviceId
                             && candidate.RevokedAtUtc is null))
                {
                    previous.Connected = false;
                    previous.RevokedAtUtc = DateTimeOffset.UtcNow;
                }
            }
            SaveLocked();
        }
    }

    public IReadOnlyList<DeviceView> ListDevices(IEnumerable<string> rooms, TimeSpan offlineAfter)
    {
        var now = DateTimeOffset.UtcNow;
        var roomSet = rooms.Select(RoomId.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            return roomSet.Select(room =>
            {
                var device = _state.Devices
                    .Where(candidate => candidate.RoomId == room && candidate.RevokedAtUtc is null)
                    .OrderByDescending(candidate => candidate.Connected)
                    .ThenByDescending(candidate => candidate.LastSeenAtUtc)
                    .ThenByDescending(candidate => candidate.RegisteredAtUtc)
                    .FirstOrDefault();
                return device is null
                    ? DeviceView.Empty(room)
                    : DeviceView.From(device, device.Connected && device.LastSeenAtUtc is { } seen
                        && now - seen <= offlineAfter);
            }).OrderBy(view => view.RoomId).ToArray();
        }
    }

    public DeviceView? GetDevice(string roomId, TimeSpan offlineAfter)
    {
        var room = RoomId.Normalize(roomId);
        return ListDevices([room], offlineAfter).SingleOrDefault();
    }

    public bool HasReported(DeviceIdentity identity)
    {
        lock (_gate)
        {
            var device = FindLocked(identity);
            return device?.Inspection is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null };
        }
    }

    public string AddCommand(string roomId, string action, JsonElement payload, string requestedBy)
    {
        var commandId = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _state.Commands.Add(new ControlCommandAudit
            {
                CommandId = commandId, RoomId = RoomId.Normalize(roomId), Action = action,
                Payload = payload.Clone(), RequestedBy = requestedBy, RequestedAtUtc = DateTimeOffset.UtcNow,
                State = "queued"
            });
            TrimCommandsLocked();
            SaveLocked();
        }
        return commandId;
    }

    public void CompleteCommand(string commandId, CentralCommandResult result)
    {
        lock (_gate)
        {
            var command = _state.Commands.FirstOrDefault(candidate => candidate.CommandId == commandId);
            if (command is null) return;
            command.State = result.Ok ? "completed" : "failed";
            command.CompletedAtUtc = result.CompletedAtUtc;
            command.StatusCode = result.StatusCode;
            command.Error = result.Error;
            SaveLocked();
        }
    }

    public void FailCommand(string commandId, string error)
    {
        lock (_gate)
        {
            var command = _state.Commands.FirstOrDefault(candidate => candidate.CommandId == commandId);
            if (command is null) return;
            command.State = "failed";
            command.CompletedAtUtc = DateTimeOffset.UtcNow;
            command.Error = error;
            SaveLocked();
        }
    }

    public PackageIdentity Package(ControlPlaneOptions options)
    {
        lock (_gate) return ResolvePackage(options.PreparedPackagePath);
    }

    private RegisteredDevice? FindLocked(DeviceIdentity identity) => _state.Devices.FirstOrDefault(candidate =>
        candidate.RoomId == identity.RoomId && candidate.DeviceId == identity.DeviceId
        && candidate.RevokedAtUtc is null);

    private ControlPlaneState Load()
    {
        if (!File.Exists(_path)) return new ControlPlaneState();
        try
        {
            return JsonSerializer.Deserialize<ControlPlaneState>(File.ReadAllText(_path), Json)
                ?? new ControlPlaneState();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Control-plane state is invalid: {_path}", ex);
        }
    }

    private void SaveLocked()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        File.Move(temp, _path, true);
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        _state.Enrollments.RemoveAll(ticket => ticket.ExpiresAtUtc <= now
            || ticket.UsedAtUtc is not null && now - ticket.UsedAtUtc > TimeSpan.FromDays(1));
        _state.Devices.RemoveAll(device => device.RevokedAtUtc is not null && now - device.RevokedAtUtc > TimeSpan.FromDays(7)
            || device.RevokedAtUtc is null && !device.Connected && device.LastSeenAtUtc is null
            && now - device.RegisteredAtUtc > TimeSpan.FromDays(1));
    }

    private void TrimCommandsLocked()
    {
        if (_state.Commands.Count <= 500) return;
        _state.Commands = _state.Commands.OrderByDescending(command => command.RequestedAtUtc).Take(500).ToList();
    }

    private PackageIdentity ResolvePackage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new PackageIdentity(string.Empty, 0, string.Empty);
        var info = new FileInfo(path);
        var fullPath = info.FullName;
        if (string.Equals(_packageCache.Path, fullPath, StringComparison.OrdinalIgnoreCase)
            && _packageCache.Length == info.Length && _packageCacheLastWriteUtc == info.LastWriteTimeUtc)
            return _packageCache;
        using var stream = File.OpenRead(path);
        _packageCache = new PackageIdentity(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), stream.Length, fullPath);
        _packageCacheLastWriteUtc = info.LastWriteTimeUtc;
        return _packageCache;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed class ControlPlaneState
{
    public int SchemaVersion { get; set; } = 1;
    public List<EnrollmentTicket> Enrollments { get; set; } = [];
    public List<RegisteredDevice> Devices { get; set; } = [];
    public List<ControlCommandAudit> Commands { get; set; } = [];
}

public sealed class EnrollmentTicket
{
    public string Id { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
}

public sealed class RegisteredDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string RegisteredBy { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    [JsonIgnore]
    public bool Connected { get; set; }
    [JsonIgnore]
    public DateTimeOffset LastPersistedAtUtc { get; set; }
    public JsonElement? App { get; set; }
    public JsonElement? Audio { get; set; }
    public JsonElement? Inspection { get; set; }
    public JsonElement? Recovery { get; set; }
}

public sealed class ControlCommandAudit
{
    public string CommandId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string State { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}

public sealed record DeviceIdentity(string RoomId, string DeviceId, string InstallationId, string MachineName);
public sealed record PackageIdentity(string Hash, long Length, string Path);

public sealed record DeviceView(
    string RoomId, bool Registered, bool Online, string DeviceId, string InstallationId, string MachineName,
    string AgentVersion, DateTimeOffset? RegisteredAtUtc, DateTimeOffset? LastSeenAtUtc,
    JsonElement? App, JsonElement? Audio, JsonElement? Inspection, JsonElement? Recovery)
{
    public static DeviceView Empty(string room) => new(room, false, false, string.Empty, string.Empty, string.Empty,
        string.Empty, null, null, null, null, null, null);

    public static DeviceView From(RegisteredDevice device, bool online) => new(device.RoomId, true, online,
        device.DeviceId, device.InstallationId, device.MachineName, device.AgentVersion, device.RegisteredAtUtc,
        device.LastSeenAtUtc, device.App, device.Audio, device.Inspection, device.Recovery);
}
