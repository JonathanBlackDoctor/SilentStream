using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using SilentStream.UninstallHelper.Safety;

if (args.Length == 6 && args[0] == "--watch-app" &&
    int.TryParse(args[1], out var watchParent) && long.TryParse(args[2], out var watchStarted))
    return await SilentStream.UninstallHelper.ApplicationGuardian.WatchAsync(watchParent, watchStarted, args[3], args[4], args[5]);

if (args.Length is 5 or 9 && args[0] == "--restart-after" &&
    int.TryParse(args[1], out var restartParent) && long.TryParse(args[2], out var restartStarted))
{
    var result = await SilentStream.UninstallHelper.ApplicationRestart.RestartAfterExitAsync(
        restartParent, restartStarted, args[3], args.Length == 9 ? () => {
            var safety = new ApplicationSafetySession(args[5], string.Empty);
            return long.TryParse(args[8], out var revision) && safety.ConsumeRestart(args[6], args[7], revision);
        } : null, args.Length == 9 ? args[5] : ApplicationSafetySession.DefaultDirectory);
    try
    {
        var resultPath = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new {
            requestedParentProcessId = restartParent, completedAtUtc = DateTimeOffset.UtcNow,
            exitCode = result, processLaunchAccepted = result == 0
        }));
    }
    catch (Exception) { /* Keep the relaunch result; receipt failure cannot justify another launch. */ }
    return result;
}

if (args.Length != 1 || !File.Exists(args[0]))
{
    return 2;
}

RemoteRemovalPayload? payload;
try
{
    payload = JsonSerializer.Deserialize<RemoteRemovalPayload>(File.ReadAllText(args[0]),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
catch (Exception)
{
    return 3;
}
if (payload is null || !File.Exists(payload.UpdateExePath) || string.IsNullOrWhiteSpace(payload.ServiceUrl) ||
    string.IsNullOrWhiteSpace(payload.CommandId) || string.IsNullOrWhiteSpace(payload.CompletionToken))
{
    return 4;
}

try
{
    if (ApplicationSafetySession.IsPreservationHeld(payload.AppDataDirectory)) return 16;
    try
    {
        using var parent = Process.GetProcessById(payload.ParentProcessId);
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    }
    catch (ArgumentException)
    {
        // The process already exited, which is the normal fast path.
    }

    using var updater = Process.Start(new ProcessStartInfo
    {
        FileName = payload.UpdateExePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "uninstall", "--silent" }
    });
    if (updater is null) return 5;
    await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    if (updater.ExitCode != 0) return 5;

    if (ApplicationSafetySession.IsPreservationHeld(payload.AppDataDirectory)) return 16;
    try { if (Directory.Exists(payload.AppDataDirectory)) Directory.Delete(payload.AppDataDirectory, recursive: true); }
    catch (IOException) { return 6; }
    catch (UnauthorizedAccessException) { return 6; }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var endpoint = new Uri(new Uri(payload.ServiceUrl.EndsWith('/') ? payload.ServiceUrl : payload.ServiceUrl + "/"),
        $"api/device-removal-commands/{Uri.EscapeDataString(payload.CommandId)}/complete");
    using var response = await http.PostAsJsonAsync(endpoint, new { completionToken = payload.CompletionToken });
    return response.IsSuccessStatusCode ? 0 : 7;
}
finally
{
    try { File.Delete(args[0]); } catch (IOException) { }
}

internal sealed record RemoteRemovalPayload(
    int ParentProcessId,
    string UpdateExePath,
    string AppDataDirectory,
    string ServiceUrl,
    string CommandId,
    string CompletionToken);
