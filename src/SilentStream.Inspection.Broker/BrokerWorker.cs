using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SilentStream.Inspection;
using SilentStream.Core.Safety;

namespace SilentStream.Inspection.Broker;

public sealed class BrokerWorker : BackgroundService
{
    private static readonly HashSet<string> AllowedProcesses = new(StringComparer.OrdinalIgnoreCase) { "MediaCaptureHelper", "ffmpeg", "cloudflared" };
    private readonly BrokerConfiguration _configuration;
    private readonly UsbReadCollector _usb;
    private readonly ILogger<BrokerWorker> _logger;
    private readonly ISystemPowerSafetyProbe _powerSafety;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly SemaphoreSlim _rebootGate = new(1, 1);

    public BrokerWorker(BrokerConfiguration configuration, ILogger<BrokerWorker> logger,
        ISystemPowerSafetyProbe powerSafety, UsbReadCollector usb)
    {
        _configuration = configuration;
        _usb = usb;
        _logger = logger;
        _powerSafety = powerSafety;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            MarkSuccessfulBootIfNeeded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogCritical(ex,
                "The durable reboot safety state is unavailable. Reboot requests will remain fail-closed.");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = CreatePipe();
            try { await pipe.WaitForConnectionAsync(stoppingToken); _ = HandleClientAsync(pipe, stoppingToken); }
            catch { pipe.Dispose(); if (stoppingToken.IsCancellationRequested) return; }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(CoordinatorServiceSid(), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(InspectionProtocol.BrokerPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16 * 1024, 16 * 1024, security);
    }

    private static SecurityIdentifier CoordinatorServiceSid()
    {
        try { return (SecurityIdentifier)new NTAccount("NT SERVICE", "SilentStreamInspectionCoordinator").Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException ex) { throw new InvalidOperationException("Coordinator service SID is unavailable; refusing to create a broader broker pipe ACL.", ex); }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            BrokerRequest? request = null;
            try
            {
                request = await ReadAsync<BrokerRequest>(pipe, token);
                if (!Authenticate(request)) throw new UnauthorizedAccessException("Broker authentication failed.");
                var response = await ExecuteRequestAsync(request, token);
                await WriteAsync(pipe, response, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Broker request failed.");
                try { await WriteAsync(pipe, new BrokerResponse(request?.Id ?? string.Empty, false, ex.Message), token); } catch { }
            }
        }
    }

    private bool Authenticate(BrokerRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.ConfirmationId)
            || request.RequestedAtUtc < now.AddSeconds(-30) || request.RequestedAtUtc > now.AddSeconds(5)
            || !_seen.TryAdd(request.Id, now)) return false;
        foreach (var old in _seen.Where(p => p.Value < DateTimeOffset.UtcNow.AddMinutes(-10)).ToArray()) _seen.TryRemove(old.Key, out _);
        var payload = request.Payload is null ? string.Empty : JsonSerializer.Serialize(request.Payload.Value, InspectionProtocol.Json);
        var body = $"{request.Id}\n{request.Action}\n{request.ConfirmationId}\n{request.RequestedAtUtc:O}\n{payload}";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_configuration.Secret), Encoding.UTF8.GetBytes(body));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(request.Auth)); }
        catch (FormatException) { return false; }
    }

    private async Task<BrokerResponse> ExecuteRequestAsync(BrokerRequest request, CancellationToken token)
    {
        switch (request.Action)
        {
            case "usb-documents-poll":
                var payload = request.Payload;
                var batch = _usb.Poll(payload?.GetProperty("enabled").GetBoolean() == true,
                    payload?.GetProperty("cursor").GetInt64() ?? 0);
                return new BrokerResponse(request.Id, true, "", JsonSerializer.SerializeToElement(batch, InspectionProtocol.Json));
            case "restart-app":
                RequireFile(_configuration.AppExecutablePath, "Configured app executable");
                KillAllowed("MediaCaptureHelper");
                return Ok(request, "Application restart was approved for the interactive session.", new
                {
                    appExecutablePath = _configuration.AppExecutablePath
                });
            case "restart-cloudflared":
                KillAllowed("cloudflared");
                return Ok(request, "Cloudflare tunnel restart was requested.");
            case "kill-process":
                var name = RequiredString(request.Payload, "name");
                if (!AllowedProcesses.Contains(name)) throw new InvalidDataException("Process is not in the fault-injection allowlist.");
                var count = KillAllowed(name);
                return Ok(request, $"Stopped {count} matching process(es).");
            case "nic-bounce":
                var adapter = RequiredString(request.Payload, "adapter");
                if (adapter == "$active") adapter = ResolveActiveAdapter();
                var seconds = GetInt(request.Payload, "disableSeconds", 30, 5, 30);
                if (!NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(adapter, StringComparison.Ordinal))) throw new InvalidDataException("Network adapter name is not exact.");
                await BounceNicAsync(adapter, seconds, token);
                return Ok(request, "Network adapter was restored after the bounded interruption.");
            case "reboot":
                await ScheduleSafeRebootAsync(request, token);
                return Ok(request, "Safety checks passed. Reboot is scheduled in 60 seconds and can be cancelled with shutdown /a.");
            case "install-app":
                var installerHash = await VerifyCachedInstallerAsync(token);
                return Ok(request, "Cached installer was verified for the interactive session.", new
                {
                    installerPath = _configuration.CachedInstallerPath,
                    sha256 = installerHash
                });
            case "uninstall-app":
                var approved = await ApproveUninstallerAsync(token);
                return Ok(request, "Application uninstall was approved for the interactive session.", approved);
            case "apply-inspection-update":
                var updatePackage = RequiredString(request.Payload, "packagePath");
                var update = ApproveInspectionUpdate(updatePackage);
                return Ok(request, "Signed inspection-agent update was approved and scheduled.", update);
            default: throw new InvalidDataException("Privileged action is not in the broker schema.");
        }
    }

    private async Task ScheduleSafeRebootAsync(BrokerRequest request, CancellationToken token)
    {
        await _rebootGate.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = ReadRebootState();
            if (state.LastScheduledAtUtc is { } last && now - last < TimeSpan.FromHours(24))
                throw new InvalidOperationException($"Only one inspection reboot is allowed per 24 hours. Last request: {last:O}");

            var assessment = await _powerSafety.InspectAsync(token);
            if (!assessment.Safe)
            {
                var blockers = string.Join(" ", assessment.Blockers.Select(check => check.Message));
                WriteRebootAudit("blocked", request, blockers, assessment);
                throw new InvalidOperationException("Boot safety checks blocked the reboot. " + blockers);
            }

            var bootStartedAtUtc = CurrentBootStartedAtUtc();
            var reserved = new RebootSafetyState(now, request.Id, request.ConfirmationId, bootStartedAtUtc,
                AwaitingBootConfirmation: true, LastBootObservedAtUtc: state.LastBootObservedAtUtc);
            WriteRebootState(reserved);
            try
            {
                await RunAsync(Hidden(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r", "/t", "60",
                    "/d", "p:4:1", "/c",
                    "SilentStream approved safety reboot - run shutdown /a within 60 seconds to cancel."),
                    TimeSpan.FromSeconds(10), CancellationToken.None);
                WriteRebootAudit("scheduled", request,
                    "Boot safety checks passed; reboot is scheduled for 60 seconds.", assessment);
            }
            catch (Exception ex)
            {
                await TryAbortShutdownAsync();
                try { WriteRebootState(state); }
                catch (Exception rollback) { _logger.LogCritical(rollback, "Could not roll back the failed reboot reservation."); }
                try { WriteRebootAudit("schedule-failed", request, ex.GetType().Name, assessment); }
                catch (Exception audit) { _logger.LogError(audit, "Could not audit the failed reboot schedule."); }
                throw;
            }
        }
        finally
        {
            _rebootGate.Release();
        }
    }

    private static async Task TryAbortShutdownAsync()
    {
        try
        {
            await RunAsync(Hidden(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/a"),
                TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch
        {
            // Best effort only: this is used after scheduling failed or became indeterminate.
        }
    }

    private void MarkSuccessfulBootIfNeeded()
    {
        var state = ReadRebootState();
        if (!state.AwaitingBootConfirmation || state.BootSessionStartedAtUtc is null) return;
        var currentBoot = CurrentBootStartedAtUtc();
        if (currentBoot <= state.BootSessionStartedAtUtc.Value.AddMinutes(1)) return;
        var observed = state with { AwaitingBootConfirmation = false, LastBootObservedAtUtc = DateTimeOffset.UtcNow };
        WriteRebootState(observed);
        _logger.LogInformation("The inspection broker observed a successful Windows return after the planned reboot.");
    }

    private RebootSafetyState ReadRebootState()
    {
        var path = Path.Combine(_configuration.Root, "reboot-safety-state.json");
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<RebootSafetyState>(File.ReadAllText(path), InspectionProtocol.Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The durable reboot safety state could not be read.", ex);
        }
    }

    private void WriteRebootState(RebootSafetyState state)
    {
        Directory.CreateDirectory(_configuration.Root);
        var path = Path.Combine(_configuration.Root, "reboot-safety-state.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, InspectionProtocol.Json));
        File.Move(temporary, path, overwrite: true);
    }

    private void WriteRebootAudit(string stage, BrokerRequest request, string message,
        SystemPowerSafetyAssessment assessment)
    {
        Directory.CreateDirectory(_configuration.Root);
        var entry = JsonSerializer.Serialize(new
        {
            occurredAtUtc = DateTimeOffset.UtcNow,
            stage,
            requestId = request.Id,
            confirmationFingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(request.ConfirmationId))).ToLowerInvariant()[..16],
            message,
            assessment.Safe,
            assessment.Checks
        }, InspectionProtocol.Json);
        File.AppendAllText(Path.Combine(_configuration.Root, "reboot-safety-audit.jsonl"), entry + Environment.NewLine);
    }

    private static DateTimeOffset CurrentBootStartedAtUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private sealed record RebootSafetyState(
        DateTimeOffset? LastScheduledAtUtc = null,
        string? RequestId = null,
        string? ConfirmationId = null,
        DateTimeOffset? BootSessionStartedAtUtc = null,
        bool AwaitingBootConfirmation = false,
        DateTimeOffset? LastBootObservedAtUtc = null);

    private object ApproveInspectionUpdate(string packagePath)
    {
        var package = Path.GetFullPath(packagePath);
        var incoming = Path.GetFullPath(Path.Combine(_configuration.Root, "incoming-agent-updates")) + Path.DirectorySeparatorChar;
        if (!package.StartsWith(incoming, StringComparison.OrdinalIgnoreCase) || !File.Exists(package))
            throw new UnauthorizedAccessException("Agent update package is outside the incoming update directory.");
        var verifier = new InspectionAgentUpdateVerifier(_configuration.PublicKeys);
        var verified = verifier.VerifyAndExtract(package, _configuration.TrustedStagingRoot);
        if (Version.Parse(verified.Manifest.Version) <= Version.Parse(InspectionProtocol.AgentVersion))
            throw new InvalidDataException("Agent update version must be newer than the installed version.");
        Directory.CreateDirectory(_configuration.UpdateRunnerRoot);
        var runner = Path.Combine(_configuration.UpdateRunnerRoot, "SilentStreamInspectionUpdater.exe");
        File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("Broker executable path is unavailable."), runner, true);
        var start = DetachedHidden(runner, "--apply-agent-update", verified.ExtractedPath, _configuration.InstallRoot,
            _configuration.Root, verified.Manifest.Version, package);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Agent update recovery runner did not start.");
        return new { verified.Manifest.Version, verified.PackageSha256, scheduled = true };
    }

    private async Task BounceNicAsync(string adapter, int seconds, CancellationToken token)
    {
        var watchdog = Hidden(Environment.ProcessPath!, "--nic-watchdog", adapter, seconds.ToString());
        if (Process.Start(watchdog) is null) throw new InvalidOperationException("NIC recovery watchdog did not start.");
        await RunAsync(Hidden(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "interface", "set", "interface", $"name={adapter}", "admin=disabled"), TimeSpan.FromSeconds(15), token);
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
        finally { await SetNicEnabledAsync(adapter, CancellationToken.None); }
    }

    public static async Task EnableNicAfterDelayAsync(string adapter, int seconds, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 30)), token);
        await SetNicEnabledAsync(adapter, token);
    }

    private static async Task SetNicEnabledAsync(string adapter, CancellationToken token) =>
        await RunAsync(Hidden(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "interface", "set", "interface", $"name={adapter}", "admin=enabled"), TimeSpan.FromSeconds(30), token);

    private async Task<object> ApproveUninstallerAsync(CancellationToken token)
    {
        // Never remove the app unless a verified reinstall path already exists.
        var installerHash = await VerifyCachedInstallerAsync(token);
        RequireFile(_configuration.AppExecutablePath, "Configured app executable");
        var appDirectory = Directory.GetParent(_configuration.AppExecutablePath)?.FullName ?? throw new InvalidDataException("App path has no parent.");
        var updater = Path.Combine(Directory.GetParent(appDirectory)?.FullName ?? string.Empty, "Update.exe");
        RequireFile(updater, "Velopack updater");
        KillAllowed("MediaCaptureHelper");
        return new
        {
            updaterPath = updater,
            appExecutablePath = _configuration.AppExecutablePath,
            installerPath = _configuration.CachedInstallerPath,
            installerSha256 = installerHash
        };
    }

    private async Task<string> VerifyCachedInstallerAsync(CancellationToken token)
    {
        RequireFile(_configuration.CachedInstallerPath, "Cached installer");
        await using var installer = new FileStream(_configuration.CachedInstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(installer, token)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(_configuration.CachedInstallerSha256) || _configuration.CachedInstallerSha256.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(_configuration.CachedInstallerSha256)))
            throw new CryptographicException("Cached installer SHA-256 does not match the approved value.");
        return actual;
    }

    private int KillAllowed(string name)
    {
        if (!AllowedProcesses.Contains(name)) throw new InvalidDataException("Process is not allowed.");
        var expectedPath = name.ToLowerInvariant() switch
        {
            "mediacapturehelper" => Path.GetFullPath(_configuration.AppExecutablePath),
            "ffmpeg" => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_configuration.AppExecutablePath) ?? string.Empty, "ffmpeg", "ffmpeg.exe")),
            "cloudflared" => Path.GetFullPath(Path.Combine(_configuration.InstallRoot, "cloudflared", "cloudflared.exe")),
            _ => throw new InvalidDataException("Process is not allowed.")
        };
        var count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var actualPath = process.MainModule?.FileName;
                if (actualPath is null || !Path.GetFullPath(actualPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(true); count++;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return count;
    }

    private static string ResolveActiveAdapter()
    {
        var candidate = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(n => new { Interface = n, Properties = n.GetIPProperties() })
            .FirstOrDefault(n => n.Properties.GatewayAddresses.Any(g => g.Address is { } address && !System.Net.IPAddress.Any.Equals(address) && !System.Net.IPAddress.IPv6Any.Equals(address)));
        return candidate?.Interface.Name ?? throw new InvalidOperationException("No active default-route network adapter was found.");
    }

    private static ProcessStartInfo Hidden(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument); return start;
    }

    private static ProcessStartInfo DetachedHidden(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token); linked.CancelAfter(timeout);
        await process.WaitForExitAsync(linked.Token); if (process.ExitCode != 0) throw new InvalidOperationException($"Command exited with {process.ExitCode}.");
    }

    private static void RequireFile(string path, string label) { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException($"{label} is not available.", path); }
    private static string RequiredString(JsonElement? payload, string name) => payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Missing payload.{name}.");
    private static int GetInt(JsonElement? payload, string name, int fallback, int min, int max) => payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? Math.Clamp(number, min, max) : fallback;
    private static BrokerResponse Ok(BrokerRequest request, string message, object? payload = null) => new(request.Id, true, message,
        payload is null ? null : JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json));

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token) { var header=new byte[4];await stream.ReadExactlyAsync(header,token);var length=BitConverter.ToInt32(header);if(length is <1 or >1024*1024)throw new InvalidDataException("Invalid pipe message.");var data=new byte[length];await stream.ReadExactlyAsync(data,token);return JsonSerializer.Deserialize<T>(data,InspectionProtocol.Json)??throw new InvalidDataException("Empty pipe message."); }
    private static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token){var data=JsonSerializer.SerializeToUtf8Bytes(value,InspectionProtocol.Json);await stream.WriteAsync(BitConverter.GetBytes(data.Length),token);await stream.WriteAsync(data,token);await stream.FlushAsync(token);}
}
