using System.Text.Json;
using System.IO;
using SilentStream.Core;

namespace SilentStream.App.Remote;

/// <summary>
/// Bridges the unattended field-check scripts and the authenticated phone controller.
/// The scripts can always write a local status file even when the embedded server is restarting;
/// commands are a deliberately small allow-list, never arbitrary shell input.
/// </summary>
public sealed class InspectionRemoteStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "retry-stream", "restart-app", "rerun-check"
    };

    private readonly object _gate = new();

    public object GetSnapshot()
    {
        lock (_gate)
        {
            return new
            {
                available = File.Exists(AppPaths.InspectionStatusFile),
                state = Read<InspectionState>(AppPaths.InspectionStatusFile),
                command = Read<InspectionCommand>(AppPaths.InspectionCommandFile)
            };
        }
    }

    public InspectionReportResult SaveReport(InspectionReportRequest report)
    {
        lock (_gate)
        {
            var old = Read<InspectionState>(AppPaths.InspectionStatusFile);
            var state = new InspectionState(
                RunId: report.RunId ?? old?.RunId ?? string.Empty,
                Room: report.Room ?? old?.Room ?? string.Empty,
                Phase: report.Phase ?? old?.Phase ?? "running",
                ProgressPercent: Math.Clamp(report.ProgressPercent, 0, 100),
                CurrentStep: report.CurrentStep ?? string.Empty,
                Message: report.Message ?? string.Empty,
                Severity: NormalizeSeverity(report.Severity),
                StartedAtUtc: report.StartedAtUtc ?? old?.StartedAtUtc ?? DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                ResultsPath: report.ResultsPath ?? old?.ResultsPath ?? string.Empty,
                RecentLog: report.RecentLog?.TakeLast(20).ToArray() ?? old?.RecentLog ?? [],
                Issues: report.Issues?.TakeLast(20).ToArray() ?? old?.Issues ?? [],
                RerunCount: Math.Max(0, report.RerunCount ?? old?.RerunCount ?? 0),
                NotificationKey: old?.NotificationKey);

            var key = NotificationKey(state);
            var notify = key is not null && !string.Equals(key, old?.NotificationKey, StringComparison.Ordinal);
            if (notify)
            {
                state = state with { NotificationKey = key };
            }

            WriteAtomic(AppPaths.InspectionStatusFile, state);
            return new InspectionReportResult(state, notify);
        }
    }

    public InspectionCommandResult QueueCommand(InspectionCommandRequest? request)
    {
        var action = request?.Action?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action) || !AllowedActions.Contains(action))
        {
            return new InspectionCommandResult(false, "허용되지 않은 점검 명령입니다.", null);
        }

        lock (_gate)
        {
            var active = Read<InspectionCommand>(AppPaths.InspectionCommandFile);
            if (active?.Status is "pending" or "running")
            {
                return new InspectionCommandResult(false, "이전 점검 명령이 처리 중입니다.", active);
            }

            var command = new InspectionCommand(
                Id: Guid.NewGuid().ToString("N"),
                Action: action,
                Status: "pending",
                RequestedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                CompletedAtUtc: null,
                Message: "원격 작업 요청을 점검 PC에 전달했습니다.");
            WriteAtomic(AppPaths.InspectionCommandFile, command);
            return new InspectionCommandResult(true, command.Message, command);
        }
    }

    private static string NormalizeSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        "success" => "success",
        _ => "info"
    };

    private static string? NotificationKey(InspectionState state)
    {
        var terminal = state.Phase is "completed" or "failed" or "post-reboot-complete";
        if (state.Severity != "error" && !terminal)
        {
            return null;
        }
        return $"{state.RunId}|{state.Phase}|{state.Severity}|{state.Message}";
    }

    private static T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                : default;
        }
        catch (JsonException)
        {
            return default; // a script may be in the middle of an interrupted old-style write
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
}

public sealed record InspectionReportRequest(
    string? RunId,
    string? Room,
    string? Phase,
    int ProgressPercent,
    string? CurrentStep,
    string? Message,
    string? Severity,
    string? StartedAtUtc,
    string? ResultsPath,
    IReadOnlyList<string>? RecentLog,
    IReadOnlyList<string>? Issues,
    int? RerunCount);

public sealed record InspectionState(
    string RunId,
    string Room,
    string Phase,
    int ProgressPercent,
    string CurrentStep,
    string Message,
    string Severity,
    string StartedAtUtc,
    string UpdatedAtUtc,
    string ResultsPath,
    IReadOnlyList<string> RecentLog,
    IReadOnlyList<string> Issues,
    int RerunCount,
    string? NotificationKey);

public sealed record InspectionCommandRequest(string? Action);

public sealed record InspectionCommand(
    string Id,
    string Action,
    string Status,
    string RequestedAtUtc,
    string? CompletedAtUtc,
    string Message);

public sealed record InspectionReportResult(InspectionState State, bool ShouldNotify);

public sealed record InspectionCommandResult(bool Ok, string Message, InspectionCommand? Command);
