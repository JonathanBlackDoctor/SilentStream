using System.Diagnostics;
using System.Text.Json;
using SilentStream.UninstallHelper.Safety;

namespace SilentStream.UninstallHelper;

public static class ApplicationGuardian
{
    public static async Task<int> WatchAsync(int parentId, long parentStartedUtcTicks,
        string executable, string directory, string runId)
    {
        if (parentId <= 0 || parentStartedUtcTicks <= 0 || !Path.IsPathFullyQualified(executable) ||
            !Path.IsPathFullyQualified(directory) || string.IsNullOrWhiteSpace(runId) ||
            !string.Equals(Path.GetFileName(executable), "MediaCaptureHelper.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable)) return 10;
        var result = "suppressed";
        try
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentStartedUtcTicks) return 11;
                await parent.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (ArgumentException) { /* Already exited: the durable exact generation must still match. */ }

            // Let broken input pipes and the old encoder finalise before creating another part.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            using var execution = ApplicationSafetySession.AcquireExecutionLease(directory, maintenance: false);
            if (execution is null) { result = "maintenance-in-progress"; return 15; }
            var safety = new ApplicationSafetySession(directory, string.Empty);
            var claim = safety.ClaimCrashRecovery(runId, parentId, parentStartedUtcTicks, DateTimeOffset.UtcNow);
            if (claim is null) return 0;
            // The claim is durable before launch. An ambiguous Process.Start outcome is never retried.
            if (safety.PreservationHeld) { result = "preservation-hold"; return 16; }
            if (safety.Snapshot is not { } latest || latest.RunId != claim.RunId || latest.Revision != claim.Revision ||
                latest.TransitionToken != claim.TransitionToken || latest.Transition != ApplicationTransition.RecoveryLaunch)
            { result = "superseded-generation"; return 0; }
            using var child = Process.Start(new ProcessStartInfo(executable) {
                UseShellExecute = false, CreateNoWindow = true
            });
            result = child is null ? "launch-failed" : "launch-requested-not-health-verified";
            return child is null ? 12 : 0;
        }
        catch { result = "observer-or-launch-failed"; return 14; }
        finally
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "app-watchdog-result.json"), JsonSerializer.Serialize(new {
                    runId, parentId, parentStartedUtcTicks, observedAtUtc = DateTimeOffset.UtcNow, result,
                    recordingVerified = false
                }));
            }
            catch { /* No second launch to repair a missing observation receipt. */ }
        }
    }
}
