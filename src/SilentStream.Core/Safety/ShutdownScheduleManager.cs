using System.Text.Json;

namespace SilentStream.Core.Safety;

public sealed record ShutdownScheduleState(string Id, string ActorId, DateTimeOffset TargetUtc,
    string Status, string Message);
public sealed record ShutdownScheduleResult(bool Ok, string Message, DateTimeOffset? CancelUntilUtc = null);

/// <summary>Durable one-shot scheduling. A claimed execution is never replayed after a crash.</summary>
public sealed class ShutdownScheduleManager
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(2);
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ShutdownScheduleState? _state;
    private bool _available = true;
    public ShutdownScheduleState? State => _state;
    public event Action? Changed;
    public bool Active => _state is { Status: "pending" or "executing" }
        || _state is { Status: "scheduled" } state && state.TargetUtc > _time.GetUtcNow();

    public ShutdownScheduleManager(string path, TimeProvider? time = null, bool nativeShutdownPending = true)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        try
        {
            if (!File.Exists(path)) return;
            _state = JsonSerializer.Deserialize<ShutdownScheduleState>(File.ReadAllText(path))
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(_state.Id) || string.IsNullOrWhiteSpace(_state.ActorId)
                || _state.TargetUtc == default
                || _state.Status is not ("pending" or "executing" or "scheduled" or "failed" or "missed" or "cancelled"))
                throw new JsonException("Invalid shutdown schedule");
            if (_state.Status == "executing")
                Save(_state with { Status = "failed", Message = "종료 준비 중 앱이 재시작되었습니다. 결과를 확인하고 다시 예약하세요." });
            else if (_state.Status == "scheduled" && !nativeShutdownPending)
                Save(_state with { Status = "failed", Message = "Windows 종료 대기가 유지되지 않았습니다. PC 상태를 확인하고 다시 예약하세요." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _available = false;
            _state = new("", "", _time.GetUtcNow(), "failed", "종료 예약 파일을 읽을 수 없습니다. PC에서 확인이 필요합니다.");
        }
    }

    public ShutdownScheduleResult Schedule(DateTimeOffset target, string actor)
    {
        if (!_gate.Wait(0)) return new(false, "종료 준비 중입니다. 현재 예약을 먼저 취소하세요.");
        try
        {
            if (!_available) return new(false, State?.Message ?? "예약 저장소를 사용할 수 없습니다.");
            if (Active) return new(false, "기존 종료 예약을 취소한 뒤 새로 예약하세요.");
            var remaining = target - _time.GetUtcNow();
            if (remaining < TimeSpan.FromMinutes(3) || remaining > TimeSpan.FromDays(30))
                return new(false, "현재부터 3분 이후, 30일 이내의 시각을 선택하세요.");
            Save(new(Guid.NewGuid().ToString("N"), actor, target.ToUniversalTime(), "pending", "PC 종료가 예약되었습니다."));
            return new(true, State!.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "종료 예약을 저장하지 못했습니다. 예약되지 않았습니다."); }
        finally { _gate.Release(); }
    }

    public async Task PollAsync(Func<ShutdownScheduleState, Task<ShutdownScheduleResult>> execute)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!_available) return;
            var now = _time.GetUtcNow();
            if (_state is { Status: "scheduled" } elapsed && now >= elapsed.TargetUtc.AddSeconds(90))
                Save(elapsed with { Status = "failed", Message = "종료 시각 뒤에도 앱이 실행 중입니다. PC 종료 결과를 확인하세요." });
            if (_state is not { Status: "pending" } state) return;
            if (now < state.TargetUtc - LeadTime) return;
            if (now >= state.TargetUtc)
            {
                Save(state with { Status = "missed", Message = "앱 종료·절전 등으로 예약 시각을 놓쳤습니다. 자동으로 뒤늦게 종료하지 않습니다." });
                return;
            }
            Save(state with { Status = "executing", Message = "예약 종료를 위해 녹화 마감과 안전 점검을 진행합니다." });
            var result = await execute(state).ConfigureAwait(false);
            Save(state with { Status = result.Ok ? "scheduled" : "failed", Message = result.Message,
                TargetUtc = result.CancelUntilUtc ?? state.TargetUtc });
        }
        catch (Exception)
        {
            // Never retry an indeterminate power action automatically.
            if (_state is { } state) _state = state with { Status = "failed", Message = "종료 예약 처리에 실패했습니다. PC 상태를 확인하세요." };
            _available = false;
            NotifyChanged();
        }
        finally { _gate.Release(); }
    }

    public async Task<ShutdownScheduleResult> CancelAsync(string? expectedId,
        Func<Task<ShutdownScheduleResult>> cancelWindows)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is not { } state || expectedId != state.Id)
                return new(false, "예약이 변경되었습니다. 상태를 새로 확인하세요.");
            if (state.Status is "scheduled" or "failed")
            {
                var result = await cancelWindows().ConfigureAwait(false);
                if (!result.Ok) return result;
            }
            else if (state.Status != "pending") return new(false, "취소할 종료 예약이 없습니다.");
            Save(state with { Status = "cancelled", Message = "PC 종료 예약을 취소했습니다." });
            return new(true, State!.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "취소 상태를 저장하지 못했습니다. PC에서 예약 상태를 확인하세요."); }
        finally { _gate.Release(); }
    }

    private void Save(ShutdownScheduleState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(_path + ".tmp", _path, true);
        _state = state;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* A disconnected status observer must not affect the power action. */ }
    }
}
