using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Broker;

public static class AgentUpdateApplier
{
    private const string CoordinatorService = "SilentStreamInspectionCoordinator";
    private const string BrokerService = "SilentStreamInspectionBroker";
    private const string SessionTask = "SilentStream Inspection Session Agent";

    public static async Task ApplyAsync(string stagedPath, string installPath, string dataPath, string expectedVersion, string packagePath)
    {
        await Task.Delay(1500).ConfigureAwait(false);
        var stage = Path.GetFullPath(stagedPath);
        var install = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
        var data = Path.GetFullPath(dataPath).TrimEnd(Path.DirectorySeparatorChar);
        var package = Path.GetFullPath(packagePath);
        EnsureUnder(package, Path.Combine(data, "incoming-agent-updates"));
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd(Path.DirectorySeparatorChar);
        EnsureUnder(install, programFiles);
        _ = Version.Parse(expectedVersion);
        var parent = Directory.GetParent(install)?.FullName ?? throw new InvalidDataException("Install root has no parent.");
        EnsureUnder(stage, Path.Combine(parent, "InspectionAgentStaging"));
        var next = Path.Combine(parent, Path.GetFileName(install) + ".next");
        var rollback = Path.Combine(parent, Path.GetFileName(install) + ".rollback");
        EnsureUnder(next, parent); EnsureUnder(rollback, parent);
        var resultDirectory = Path.Combine(data, "update-results");
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, $"agent-update-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
        var phase = "prepare";
        try
        {
            if (Directory.Exists(next)) Directory.Delete(next, true);
            CopyDirectory(stage, next, InspectionAgentUpdateVerifier.ManifestName);
            RequirePayload(next);
            // Check again at the last destructive boundary: the app may have started while the
            // signed update was being copied. Do not enter rollback/stop logic on this deferral.
            if (AgentUpdateSafety.ApplicationIsRunning())
            {
                WriteResult(resultPath, false, expectedVersion, "deferred", "방송 앱 실행 중 — 도우미 업데이트 보류", rollback);
                if (package.EndsWith(".applying", StringComparison.OrdinalIgnoreCase) && File.Exists(package))
                    File.Move(package, package[..^".applying".Length], overwrite: false);
                return;
            }
            if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
            phase = "stop";
            await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/End", "/TN", SessionTask).ConfigureAwait(false);
            foreach (var process in Process.GetProcessesByName("SilentStreamInspectionSessionAgent")) { try { process.Kill(true); } catch { } finally { process.Dispose(); } }
            await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop", CoordinatorService).ConfigureAwait(false);
            await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop", BrokerService).ConfigureAwait(false);
            await Task.Delay(2500).ConfigureAwait(false);
            phase = "swap";
            Directory.Move(install, rollback);
            Directory.Move(next, install);
            phase = "start";
            await StartAllAsync().ConfigureAwait(false);
            phase = "health";
            if (!await WaitForHealthAsync(expectedVersion, TimeSpan.FromSeconds(60)).ConfigureAwait(false))
                throw new InvalidOperationException("Updated Coordinator did not become healthy with the expected version.");
            WriteResult(resultPath, true, expectedVersion, phase, null, rollback);
            MoveMarker(package, ".applied");
        }
        catch (Exception ex)
        {
            if (phase == "prepare")
            {
                // No service was stopped yet. A copy/check failure must not stop a healthy app
                // or restore an obsolete leftover rollback directory.
                WriteResult(resultPath, false, expectedVersion, phase, ex.Message, rollback);
                MoveMarker(package, ".failed");
                return;
            }
            var rollbackError = string.Empty;
            try
            {
                await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop", CoordinatorService).ConfigureAwait(false);
                await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop", BrokerService).ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);
                if (Directory.Exists(rollback))
                {
                    if (Directory.Exists(install)) Directory.Delete(install, true);
                    Directory.Move(rollback, install);
                }
                await StartAllAsync().ConfigureAwait(false);
            }
            catch (Exception rollbackException) { rollbackError = rollbackException.Message; }
            WriteResult(resultPath, false, expectedVersion, phase, $"{ex.Message} {rollbackError}".Trim(), rollback);
            MoveMarker(package, ".failed");
        }
    }

    private static async Task StartAllAsync()
    {
        await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start", BrokerService).ConfigureAwait(false);
        await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start", CoordinatorService).ConfigureAwait(false);
        await RunTolerantAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Run", "/TN", SessionTask).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForHealthAsync(string version, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var end = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < end)
        {
            try
            {
                var health = await client.GetFromJsonAsync<JsonElement>("http://127.0.0.1:8790/inspect/api/v1/health").ConfigureAwait(false);
                if (health.TryGetProperty("ok", out var ok) && ok.GetBoolean() && health.GetProperty("version").GetString() == version) return true;
            }
            catch { }
            await Task.Delay(1500).ConfigureAwait(false);
        }
        return false;
    }

    private static void RequirePayload(string root)
    {
        foreach (var relative in new[]
        {
            @"coordinator\SilentStreamInspectionCoordinator.exe",
            @"broker\SilentStreamInspectionBroker.exe",
            @"session\SilentStreamInspectionSessionAgent.exe"
        }) if (!File.Exists(Path.Combine(root, relative))) throw new FileNotFoundException($"Updated component is missing: {relative}");
    }

    private static void CopyDirectory(string source, string destination, string excludedFile)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals(excludedFile, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }

    private static async Task RunTolerantAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start); if (process is not null) await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static void EnsureUnder(string value, string root)
    {
        var full = Path.GetFullPath(value);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException($"Path is outside its approved root: {full}");
    }

    private static void WriteResult(string path, bool success, string version, string phase, string? error, string rollbackPath) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { success, version, phase, error, rollbackPath, completedAtUtc = DateTimeOffset.UtcNow },
            new JsonSerializerOptions(InspectionProtocol.Json) { WriteIndented = true }));

    private static void MoveMarker(string package, string suffix)
    {
        try { if (File.Exists(package)) File.Move(package, package + suffix, true); } catch { }
    }
}
