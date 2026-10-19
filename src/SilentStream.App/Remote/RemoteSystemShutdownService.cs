using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.Core.Safety;

namespace SilentStream.App.Remote;

/// <summary>
/// Coordinates a fail-closed two-phase Windows shutdown. A paired client must first pass the
/// read-only boot safety probe, then confirm a short-lived nonce from the same device. The actual
/// OS command is issued without a Windows countdown, only after recording finalisation.
/// </summary>
public sealed class RemoteSystemShutdownService : IDisposable
{
    private static readonly TimeSpan ConfirmationDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IStreamOrchestrator _orchestrator;
    private readonly ILogService _log;
    private readonly ISystemPowerSafetyProbe _safety;
    private readonly object _gate = new();
    private PendingConfirmation? _pending;
    private PowerActionState _state;
    private bool _durableStateAvailable;
    private bool _shutdownScheduled;
    private QuietShutdownCountdown? _countdown;
    private bool _disposed;
    private readonly ShutdownScheduleManager _schedule;
    private readonly Timer _scheduleTimer;
    private readonly CancellationTokenSource _scheduleLifetime = new();
    public event Action? ScheduleChanged
    {
        add => _schedule.Changed += value;
        remove => _schedule.Changed -= value;
    }
    public ShutdownScheduleState? ScheduleStatus => _schedule.State;
    public bool WindowsShutdownPending => _shutdownScheduled;

    public RemoteSystemShutdownService(IStreamOrchestrator orchestrator, ILogService log,
        ISystemPowerSafetyProbe safety)
    {
        _orchestrator = orchestrator;
        _log = log;
        _safety = safety;
        _state = ReadState(out _durableStateAvailable);
        if (_state.AwaitingBootConfirmation)
        {
            var currentBoot = CurrentBootStartedAtUtc();
            if (_state.BootSessionStartedAtUtc is { } previousBoot
                && currentBoot > previousBoot.AddMinutes(1))
            {
                _state = _state with
                {
                    AwaitingBootConfirmation = false,
                    LastBootObservedAtUtc = DateTimeOffset.UtcNow
                };
                _durableStateAvailable &= WriteState(_state);
                AppendAudit("boot-observed", _state.LastActorId ?? "system",
                    "예약된 전원 전환 뒤 애플리케이션 시작을 확인했습니다.");
            }
            else if (_state.ApplicationCountdown)
            {
                // An app-owned timer cannot survive a crash. Never replay it on startup.
                _state = _state with { AwaitingBootConfirmation = false };
                _durableStateAvailable &= WriteState(_state);
                AppendAudit("countdown-interrupted", _state.LastActorId ?? "system",
                    "앱이 다시 시작되어 이전 종료 대기를 취소했습니다. 자동으로 재실행하지 않습니다.");
            }
            else if (_state.LastActionAtUtc is { } requestedAt
                     && DateTimeOffset.UtcNow > (_state.ShutdownAtUtc ?? requestedAt.Add(ShutdownDelay)).AddSeconds(30))
            {
                _state = _state with { AwaitingBootConfirmation = false };
                _durableStateAvailable &= WriteState(_state);
                AppendAudit("schedule-expired", _state.LastActorId ?? "system",
                    "같은 부팅 세션에서 종료 예약 시간이 지났으므로 예약 상태를 해제했습니다.");
            }
            else
            {
                _shutdownScheduled = true;
            }
        }
        _schedule = new ShutdownScheduleManager(Path.Combine(AppPaths.AppDataDir, "shutdown-schedule.json"),
            nativeShutdownPending: _shutdownScheduled);
        _scheduleTimer = new Timer(state => _ = _schedule.PollAsync(ExecuteScheduleAsync), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
    }

    public SystemShutdownResult Schedule(DateTimeOffset targetUtc, string actorId)
    {
        lock (_gate)
        {
            if (_pending is { } pending && pending.ExpiresAtUtc <= DateTimeOffset.UtcNow) _pending = null;
            if (_shutdownScheduled || _pending is not null)
                return new(false, "다른 종료 처리가 진행 중입니다.", null);
            var result = _schedule.Schedule(targetUtc, actorId);
            if (result.Ok) AppendAudit("future-scheduled", actorId, $"PC 종료 예약: {targetUtc:O}");
            return new(result.Ok, result.Message, null);
        }
    }

    public async Task<SystemShutdownResult> CancelScheduleAsync(string? id, string actorId)
    {
        var result = await _schedule.CancelAsync(id, async () =>
        {
            if (!_shutdownScheduled) return new ShutdownScheduleResult(true, "Windows 종료 대기가 없습니다.");
            var cancelled = await CancelAsync(actorId, CancellationToken.None).ConfigureAwait(false);
            return new ShutdownScheduleResult(cancelled.Ok, cancelled.Message);
        }).ConfigureAwait(false);
        if (result.Ok) AppendAudit("future-cancelled", actorId, result.Message);
        return new(result.Ok, result.Message, null);
    }

    private async Task<ShutdownScheduleResult> ExecuteScheduleAsync(ShutdownScheduleState schedule)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_scheduleLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        var prepared = await PrepareAsync(schedule.ActorId, timeout.Token, scheduled: true).ConfigureAwait(false);
        if (!prepared.Ok) return new(false, prepared.Message);
        var delay = prepared.NotBeforeUtc!.Value - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay.Add(TimeSpan.FromMilliseconds(100)), timeout.Token).ConfigureAwait(false);
        var result = await ConfirmAsync(prepared.ConfirmationId, schedule.ActorId, timeout.Token,
            schedule.TargetUtc).ConfigureAwait(false);
        return new(result.Ok, result.Message, result.CancelUntilUtc);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _countdown?.Dispose();
        }
        _scheduleLifetime.Cancel();
        _scheduleTimer.Dispose();
        _scheduleLifetime.Dispose();
    }

    public async Task<SystemShutdownPreparation> PrepareAsync(string actorId, CancellationToken cancellationToken,
        bool scheduled = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_durableStateAvailable)
                return FailedPreparation("전원 전환 상태를 안전하게 기록할 수 없어 PC 종료를 차단했습니다.");
            if (!scheduled && _schedule.Active)
                return FailedPreparation("기존 종료 예약을 먼저 취소하세요.");
            if (_shutdownScheduled)
                return FailedPreparation("PC 종료가 이미 예약되어 있습니다.");
            if (_pending is not null && _pending.ExpiresAtUtc > now)
                return FailedPreparation("다른 PC 종료 확인이 진행 중입니다.");
            _pending = null;
            if (_state.LastActionAtUtc is { } last && now - last < RequestCooldown)
                return FailedPreparation($"최근 전원 요청 후 10분이 지나지 않아 종료를 차단했습니다. {last:O}");
        }

        SystemPowerSafetyAssessment assessment;
        try
        {
            assessment = await _safety.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"부팅 안전 점검을 완료하지 못해 PC 종료를 차단했습니다: {ex.GetType().Name}";
            _log.Error(message, ex);
            AppendAudit("probe-failed", actorId, message);
            return FailedPreparation(message);
        }
        if (!assessment.Safe)
        {
            var message = "부팅 안전 점검을 통과하지 못해 PC 종료를 차단했습니다: " +
                          string.Join(" ", assessment.Blockers.Select(check => check.Message));
            _log.Warn(message);
            AppendAudit("blocked", actorId, message, assessment);
            return new SystemShutdownPreparation(false, null, null, null, message, assessment.Checks);
        }

        now = DateTimeOffset.UtcNow;
        var confirmation = new PendingConfirmation(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            actorId,
            now.Add(ConfirmationDelay),
            now.Add(ConfirmationLifetime),
            assessment);
        lock (_gate)
        {
            if (_shutdownScheduled || _pending is not null || !scheduled && _schedule.Active)
                return FailedPreparation("다른 PC 종료 요청이 먼저 시작되었습니다.");
            _pending = confirmation;
        }

        if (!AppendAudit("prepared", actorId, "부팅 안전 점검을 통과하고 종료 확인을 준비했습니다.", assessment))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, confirmation)) _pending = null;
                _durableStateAvailable = false;
            }
            return FailedPreparation("전원 전환 감사 기록을 저장할 수 없어 PC 종료를 차단했습니다.");
        }
        return new SystemShutdownPreparation(true, confirmation.Id, confirmation.NotBeforeUtc,
            confirmation.ExpiresAtUtc, "안전 점검을 통과했습니다. 내용을 확인한 뒤 다시 승인하세요.", assessment.Checks);
    }

    public async Task<SystemShutdownResult> ConfirmAsync(string? confirmationId, string actorId,
        CancellationToken cancellationToken, DateTimeOffset? targetUtc = null)
    {
        PendingConfirmation confirmation;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_shutdownScheduled)
                return new(false, "PC 종료가 이미 예약되어 있습니다.", null);
            if (_pending is null || string.IsNullOrWhiteSpace(confirmationId)
                                 || confirmationId.Length != _pending.Id.Length
                                 || !CryptographicOperations.FixedTimeEquals(
                                     System.Text.Encoding.UTF8.GetBytes(_pending.Id),
                                     System.Text.Encoding.UTF8.GetBytes(confirmationId)))
                return new(false, "유효한 종료 확인 번호가 없습니다. 안전 점검부터 다시 실행하세요.", null);
            if (!string.Equals(_pending.ActorId, actorId, StringComparison.Ordinal))
                return new(false, "안전 점검을 요청한 기기에서만 종료를 승인할 수 있습니다.", null);
            if (now < _pending.NotBeforeUtc)
                return new(false, "안전 점검 내용을 확인할 시간이 더 필요합니다. 잠시 뒤 다시 승인하세요.", null);
            if (now > _pending.ExpiresAtUtc)
            {
                _pending = null;
                return new(false, "종료 확인 시간이 만료되었습니다. 안전 점검부터 다시 실행하세요.", null);
            }

            confirmation = _pending;
            _pending = null;
            _shutdownScheduled = true;
        }

        try
        {
            await _orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate) _shutdownScheduled = false;
            _log.Error("PC 종료 전 송출·녹화 마감이 취소되거나 제한시간을 초과했습니다.", ex);
            AppendAudit("finalization-cancelled", actorId, ex.GetType().Name, confirmation.Assessment);
            return new(false, "송출·녹화 마감이 취소되거나 제한시간을 초과해 PC 종료를 취소했습니다.", null);
        }
        catch (Exception ex)
        {
            lock (_gate) _shutdownScheduled = false;
            _log.Error("PC 종료 전 송출·녹화를 정상적으로 마감하지 못해 종료를 취소했습니다.", ex);
            AppendAudit("finalization-failed", actorId, ex.GetType().Name, confirmation.Assessment);
            return new(false, "송출·녹화를 정상적으로 마감하지 못해 PC 종료를 취소했습니다.", null);
        }

        SystemPowerSafetyAssessment finalAssessment;
        try
        {
            finalAssessment = await _safety.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) _shutdownScheduled = false;
            return new(false, "최종 부팅 안전 점검이 취소되어 PC 종료를 차단했습니다.", null);
        }
        catch (Exception ex)
        {
            lock (_gate) _shutdownScheduled = false;
            _log.Error("최종 부팅 안전 점검을 완료하지 못해 PC 종료를 차단했습니다.", ex);
            AppendAudit("final-probe-failed", actorId, ex.GetType().Name, confirmation.Assessment);
            return new(false, "최종 부팅 안전 점검을 완료하지 못해 PC 종료를 차단했습니다.", null);
        }

        if (!finalAssessment.Safe)
        {
            lock (_gate) _shutdownScheduled = false;
            var blockers = string.Join(" ", finalAssessment.Blockers.Select(check => check.Message));
            AppendAudit("final-check-blocked", actorId, blockers, finalAssessment);
            return new(false, "최종 부팅 안전 점검에서 이상을 발견해 PC 종료를 차단했습니다: " + blockers, null);
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var previousState = _state;
        var shutdownAt = targetUtc ?? requestedAt.Add(ShutdownDelay);
        if (shutdownAt <= requestedAt)
        {
            lock (_gate) _shutdownScheduled = false;
            return new(false, "예약 시각을 놓쳐 PC를 종료하지 않았습니다.", null);
        }
        var reservedState = _state with
        {
            LastActionAtUtc = requestedAt,
            LastActorId = actorId,
            AwaitingBootConfirmation = true,
            BootSessionStartedAtUtc = CurrentBootStartedAtUtc(),
            ShutdownAtUtc = shutdownAt,
            ApplicationCountdown = true
        };
        if (!WriteState(reservedState))
        {
            lock (_gate)
            {
                _shutdownScheduled = false;
                _durableStateAvailable = false;
            }
            AppendAudit("reservation-failed", actorId, "전원 전환 상태를 저장하지 못했습니다.", finalAssessment);
            return new(false, "전원 전환 상태를 안전하게 기록할 수 없어 PC 종료를 차단했습니다.", null);
        }
        _state = reservedState;

        try
        {
            QuietShutdownCountdown countdown;
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException("앱 종료로 PC 종료 대기를 만들지 않았습니다.");
                _countdown = countdown = new QuietShutdownCountdown(shutdownAt, RunShutdownCommandAsync);
            }
            _ = ObserveCountdownAsync(countdown, actorId, shutdownAt, finalAssessment);
            var message = targetUtc.HasValue
                ? "녹화와 방송을 마감했습니다. 예약 시각에 별도 안내 없이 PC를 종료합니다. 그 전까지 취소할 수 있습니다."
                : "녹화와 방송을 마감했습니다. 지금 PC를 종료합니다.";
            AppendAudit("scheduled", actorId, message, finalAssessment);
            _log.Warn(message);
            _ = ClearExpiredScheduleIfStillRunningAsync(requestedAt, shutdownAt);
            return new(true, message, shutdownAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                            or TimeoutException)
        {
            _state = previousState;
            _durableStateAvailable = WriteState(previousState);
            lock (_gate) _shutdownScheduled = false;
            _log.Error("PC 종료 예약에 실패했습니다.", ex);
            AppendAudit("schedule-failed", actorId, ex.GetType().Name, finalAssessment);
            return new(false, "Windows 종료를 예약하지 못했습니다.", null);
        }
    }

    public async Task<SystemShutdownResult> CancelAsync(string actorId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_shutdownScheduled)
            {
                if (_pending is null) return new(false, "취소할 PC 종료 준비가 없습니다.", null);
                _pending = null;
                AppendAudit("preparation-cancelled", actorId, "PC 종료 확인 준비를 취소했습니다.");
                return new(true, "PC 종료 준비를 취소했습니다.", null);
            }
        }

        try
        {
            lock (_gate)
            {
                if (_countdown is { } countdown)
                {
                    if (!countdown.TryCancel())
                        return new(false, "이미 Windows 종료가 시작되어 취소할 수 없습니다.", null);
                    countdown.Dispose();
                    _countdown = null;
                }
                else if (_state.ApplicationCountdown)
                    return new(false, "종료 준비 중입니다. 잠시 뒤 취소를 다시 요청하세요.", null);
            }
            // Compatibility: old releases may already have registered a native Windows timer.
            if (!_state.ApplicationCountdown)
            {
                using var process = Process.Start(CreateShutdownCommand("/a"));
                if (process is null) throw new InvalidOperationException("Windows 종료 취소 명령을 시작하지 못했습니다.");
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0) return new(false, "취소할 Windows 종료 예약이 없습니다.", null);
            }
            lock (_gate)
            {
                _shutdownScheduled = false;
                _pending = null;
            }
            _state = _state with { AwaitingBootConfirmation = false };
            _durableStateAvailable = WriteState(_state);
            AppendAudit("cancelled", actorId, "예약된 Windows 종료를 취소했습니다.");
            _log.Warn("예약된 PC 종료를 원격 요청으로 취소했습니다.");
            return new(true, "예약된 PC 종료를 취소했습니다.", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error("PC 종료 취소 요청에 실패했습니다.", ex);
            return new(false, "PC 종료 취소 요청을 처리하지 못했습니다.", null);
        }
    }

    private static ProcessStartInfo CreateShutdownCommand(params string[] arguments)
    {
        var path = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var start = new ProcessStartInfo(File.Exists(path) ? path : "shutdown.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task RunShutdownCommandAsync(ProcessStartInfo command)
    {
        using var process = Process.Start(command);
        if (process is null) throw new InvalidOperationException("Windows 종료 명령을 시작하지 못했습니다.");
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows 종료 명령이 {process.ExitCode}로 종료되었습니다.");
    }

    private async Task ObserveCountdownAsync(QuietShutdownCountdown countdown, string actorId,
        DateTimeOffset targetUtc, SystemPowerSafetyAssessment assessment)
    {
        try { await countdown.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_countdown, countdown)) return;
                _shutdownScheduled = false;
                _state = _state with { AwaitingBootConfirmation = false };
                _durableStateAvailable = WriteState(_state);
            }
            AppendAudit("shutdown-failed", actorId, ex.GetType().Name, assessment);
            _log.Error("PC 종료를 실행하지 못했습니다. 원격 상태를 확인하세요.", ex);
            try
            {
                await _schedule.ReportFailureAsync(targetUtc,
                    "PC 종료를 실행하지 못했습니다. PC 상태를 확인하고 다시 예약하세요.").ConfigureAwait(false);
            }
            catch (Exception saveError) { _log.Error("종료 예약 실패 상태를 저장하지 못했습니다.", saveError); }
        }
        finally { countdown.Dispose(); }
    }

    private static SystemShutdownPreparation FailedPreparation(string message) =>
        new(false, null, null, null, message, []);

    private PowerActionState ReadState(out bool available)
    {
        try
        {
            available = true;
            if (!File.Exists(AppPaths.SystemPowerStateFile)) return new();
            var state = JsonSerializer.Deserialize<PowerActionState>(File.ReadAllText(AppPaths.SystemPowerStateFile), Json);
            if (state is null) throw new JsonException("전원 전환 상태가 비어 있습니다.");
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            available = false;
            _log.Error("전원 전환 상태를 읽지 못해 전원 요청을 차단합니다.", ex);
            return new PowerActionState();
        }
    }

    private bool WriteState(PowerActionState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            var temporary = AppPaths.SystemPowerStateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, AppPaths.SystemPowerStateFile, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("전원 전환 상태를 저장하지 못했습니다.", ex);
            return false;
        }
    }

    private bool AppendAudit(string stage, string actorId, string message,
        SystemPowerSafetyAssessment? assessment = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            var entry = JsonSerializer.Serialize(new
            {
                occurredAtUtc = DateTimeOffset.UtcNow,
                stage,
                actorId,
                message,
                safe = assessment?.Safe,
                checks = assessment?.Checks
            }, Json);
            File.AppendAllText(AppPaths.SystemPowerAuditFile, entry + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"전원 전환 감사 로그를 저장하지 못했습니다: {ex.GetType().Name}");
            return false;
        }
    }

    private async Task ClearExpiredScheduleIfStillRunningAsync(DateTimeOffset requestedAt, DateTimeOffset shutdownAt)
    {
        var delay = shutdownAt.AddSeconds(30) - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_shutdownScheduled || _state.LastActionAtUtc != requestedAt) return;
            _shutdownScheduled = false;
            _state = _state with { AwaitingBootConfirmation = false };
        }
        _durableStateAvailable = WriteState(_state);
        AppendAudit("schedule-expired", _state.LastActorId ?? "system",
            "종료 예약 뒤에도 같은 부팅 세션이 계속되어 예약 상태를 해제했습니다.");
    }

    private static DateTimeOffset CurrentBootStartedAtUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private sealed record PendingConfirmation(string Id, string ActorId, DateTimeOffset NotBeforeUtc,
        DateTimeOffset ExpiresAtUtc, SystemPowerSafetyAssessment Assessment);

    private sealed record PowerActionState(
        DateTimeOffset? LastActionAtUtc = null,
        string? LastActorId = null,
        bool AwaitingBootConfirmation = false,
        DateTimeOffset? LastBootObservedAtUtc = null,
        DateTimeOffset? BootSessionStartedAtUtc = null,
        DateTimeOffset? ShutdownAtUtc = null,
        bool ApplicationCountdown = false);
}

public sealed record SystemShutdownPreparation(
    bool Ok,
    string? ConfirmationId,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Message,
    IReadOnlyList<SystemPowerSafetyCheck> Checks);

public sealed record SystemShutdownResult(bool Ok, string Message, DateTimeOffset? CancelUntilUtc);
