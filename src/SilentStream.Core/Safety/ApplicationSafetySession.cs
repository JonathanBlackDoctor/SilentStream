using System.Text.Json;

#if APP_COMPANION
namespace SilentStream.UninstallHelper.Safety;
#else
namespace SilentStream.Core.Safety;
#endif

public enum DesiredRunMode { Stopped, RecordingOnly, Live }
public enum ApplicationTransition { None, Restart, Update, PowerShutdown, Uninstall, CleanExit, RecoveryLaunch }

public sealed record ApplicationRunState(
    string RunId, int ProcessId, long ProcessStartUtcTicks, string BootId,
    DesiredRunMode DesiredMode, ApplicationTransition Transition, string? TransitionToken,
    long Revision, bool CaptureExpected, DateTimeOffset AliveAtUtc,
    DateTimeOffset RecoveryWindowUtc, int RecoveryAttempts, string Reason);

/// <summary>
/// Durable intent belongs to an exact process generation. A companion may recover only an
/// unexpected death of an active generation; manual stop, maintenance and preservation win.
/// This file is also compiled into the small restart companion (BCL dependencies only).
/// </summary>
public sealed class ApplicationSafetySession(string directory, string bootId)
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaCaptureHelper");
    public static string HoldFile(string directory) => Path.Combine(directory, "original-preservation.hold.json");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string StatePath => Path.Combine(DirectoryPath, "app-run-intent.json");
    public string? RunId { get; private set; }
    public event Action? PersistenceFailed;

    // Existence is the hold. A corrupt/unreadable marker never grants permission. Clearing it
    // requires an explicit preservation workflow; neither startup nor an upload receipt clears it.
    public static bool IsPreservationHeld(string directory)
    {
        try
        {
            using var marker = File.Open(HoldFile(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    public bool PreservationHeld => IsPreservationHeld(DirectoryPath);
    public ApplicationRunState? Snapshot
    {
        get { try { return Read(); } catch { return null; } }
    }
    public DesiredRunMode DesiredMode => Snapshot is { } state && state.RunId == RunId
        ? state.DesiredMode : DesiredRunMode.Stopped;
    public bool StartAllowed => !PreservationHeld && Snapshot is { } state && state.RunId == RunId
        && state.Transition == ApplicationTransition.None;

    public bool Begin(int processId, long startUtcTicks, bool automaticEnabled, DateTimeOffset now)
    {
        try
        {
            using var guard = Lock();
            var previous = Read(); // damaged state blocks unattended operation
            var sameBoot = !string.IsNullOrWhiteSpace(bootId) && previous?.BootId == bootId;
            var mode = sameBoot ? previous!.DesiredMode
                : automaticEnabled && !string.IsNullOrWhiteSpace(bootId) ? DesiredRunMode.Live : DesiredRunMode.Stopped;
            if (!automaticEnabled) mode = DesiredRunMode.Stopped;
            // Never replay an interrupted same-boot power countdown or removal on app relaunch.
            if (sameBoot && previous!.Transition is ApplicationTransition.PowerShutdown or ApplicationTransition.Uninstall)
                mode = DesiredRunMode.Stopped;
            if (PreservationHeld) mode = DesiredRunMode.Stopped;
            var id = Guid.NewGuid().ToString("N");
            Write(new(id, processId, startUtcTicks, bootId, mode, ApplicationTransition.None, null,
                (previous?.Revision ?? 0) + 1, false, now,
                sameBoot ? previous!.RecoveryWindowUtc : now, sameBoot ? previous!.RecoveryAttempts : 0, "startup"));
            RunId = id;
            return true;
        }
        catch { Failed(); return false; }
    }

    public bool SetDesired(DesiredRunMode mode, string reason) => Mutate(state =>
        mode != DesiredRunMode.Stopped && (PreservationHeld || state.Transition != ApplicationTransition.None)
            ? null : state with { DesiredMode = mode, Revision = state.Revision + 1, Reason = reason,
                CaptureExpected = mode == DesiredRunMode.Stopped ? false : state.CaptureExpected });

    public ApplicationRunState? BeginTransition(ApplicationTransition transition, string reason)
    {
        ApplicationRunState? result = null;
        if (transition is ApplicationTransition.None or ApplicationTransition.RecoveryLaunch or ApplicationTransition.CleanExit)
            return null;
        var persisted = Mutate(state =>
        {
            if (state.Transition != ApplicationTransition.None ||
                (PreservationHeld && transition is ApplicationTransition.Restart or ApplicationTransition.Update or ApplicationTransition.Uninstall))
                return null;
            return result = state with { Transition = transition, TransitionToken = Guid.NewGuid().ToString("N"),
                Revision = state.Revision + 1, Reason = reason };
        });
        return persisted ? result : null;
    }

    public bool CancelTransition(string token) => Mutate(state => state.TransitionToken == token
        ? state with { Transition = ApplicationTransition.None, TransitionToken = null,
            Revision = state.Revision + 1, Reason = "transition-cancelled" } : null);

    public bool Heartbeat(bool captureExpected, DateTimeOffset now) => Mutate(state => state with {
        AliveAtUtc = now, CaptureExpected = captureExpected && state.DesiredMode != DesiredRunMode.Stopped });

    public bool MarkCleanExit() => Mutate(state => state with {
        CaptureExpected = false, Revision = state.Transition == ApplicationTransition.None ? state.Revision + 1 : state.Revision,
        DesiredMode = state.Transition == ApplicationTransition.None ? DesiredRunMode.Stopped : state.DesiredMode,
        Transition = state.Transition == ApplicationTransition.None ? ApplicationTransition.CleanExit : state.Transition,
        Reason = state.Transition == ApplicationTransition.None ? "normal-app-exit" : state.Reason });

    public ApplicationRunState? ClaimCrashRecovery(string expectedRunId, int parentId, long parentStartUtcTicks, DateTimeOffset now)
    {
        try
        {
            using var guard = Lock();
            var state = Read();
            if (PreservationHeld || state is null || state.RunId != expectedRunId || state.ProcessId != parentId ||
                state.ProcessStartUtcTicks != parentStartUtcTicks || string.IsNullOrWhiteSpace(state.BootId) ||
                state.Transition != ApplicationTransition.None || !state.CaptureExpected || state.DesiredMode == DesiredRunMode.Stopped ||
                now < state.AliveAtUtc || now - state.AliveAtUtc > TimeSpan.FromMinutes(2)) return null;
            var resetWindow = now - state.RecoveryWindowUtc >= TimeSpan.FromMinutes(30);
            var attempts = resetWindow ? 0 : state.RecoveryAttempts;
            if (now < state.RecoveryWindowUtc || attempts >= 3) return null;
            var claimed = state with { Transition = ApplicationTransition.RecoveryLaunch,
                TransitionToken = Guid.NewGuid().ToString("N"), Revision = state.Revision + 1,
                RecoveryWindowUtc = resetWindow ? now : state.RecoveryWindowUtc,
                RecoveryAttempts = attempts + 1, Reason = "unexpected-process-exit" };
            Write(claimed);
            return claimed;
        }
        catch { return null; }
    }

    public bool ConsumeRestart(string expectedRunId, string token, long revision)
    {
        try
        {
            using var guard = Lock();
            var state = Read();
            if (PreservationHeld || state is null || state.RunId != expectedRunId || state.TransitionToken != token ||
                state.Revision != revision || state.Transition != ApplicationTransition.Restart) return false;
            Write(state with { Transition = ApplicationTransition.RecoveryLaunch, Revision = state.Revision + 1,
                Reason = "explicit-restart-launch" });
            return true;
        }
        catch { return false; }
    }

    public static FileStream? AcquireExecutionLease(string directory, bool maintenance)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, "app-execution.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, maintenance ? FileShare.None : FileShare.ReadWrite);
        }
        catch { return null; }
    }

    private bool Mutate(Func<ApplicationRunState, ApplicationRunState?> change)
    {
        try
        {
            using var guard = Lock();
            var state = Read();
            if (RunId is null || state?.RunId != RunId) return false;
            var updated = change(state);
            if (updated is null) return false;
            Write(updated);
            return true;
        }
        catch { Failed(); return false; }
    }
    private void Failed() { try { PersistenceFailed?.Invoke(); } catch { } }
    private FileStream Lock()
    {
        Directory.CreateDirectory(DirectoryPath);
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (attempt < 20 && (ex.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(10); }
        }
    }
    private ApplicationRunState? Read()
    {
        try
        {
            var state = JsonSerializer.Deserialize<ApplicationRunState>(File.ReadAllText(StatePath))
                ?? throw new InvalidDataException("Empty application intent.");
            if (string.IsNullOrWhiteSpace(state.RunId) || state.ProcessId <= 0 || state.ProcessStartUtcTicks <= 0 ||
                !Enum.IsDefined(state.DesiredMode) || !Enum.IsDefined(state.Transition) || state.Revision < 1 || state.RecoveryAttempts < 0)
                throw new InvalidDataException("Invalid application intent.");
            return state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private void Write(ApplicationRunState state)
    {
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, StatePath, overwrite: true);
    }
}
