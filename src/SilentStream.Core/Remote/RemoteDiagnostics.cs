using SilentStream.Core.Models;

namespace SilentStream.Core.Remote;

/// <summary>
/// Read-only diagnostics projected to the paired phone: active health conditions, the recent
/// warning/error tail, and the school-period clock. Kept platform-neutral so the wall-clock
/// policy is unit-tested without starting the WPF/Kestrel host.
/// </summary>
public static class RemoteDiagnostics
{
    public static RemoteDiagnosticsDto Build(
        DateTime nowLocal,
        DaySchedule schedule,
        IEnumerable<HealthEvent> activeHealth,
        IEnumerable<string> logLines)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(activeHealth);
        ArgumentNullException.ThrowIfNull(logLines);

        return new RemoteDiagnosticsDto(
            nowLocal,
            BuildTimeline(nowLocal, schedule),
            activeHealth
                .Select(e => new RemoteHealthDto(e.Kind.ToString(), e.Severity.ToString(), e.Message, e.TimestampUtc))
                .ToArray(),
            logLines
                .Where(IsProblemLog)
                .TakeLast(20)
                .Reverse()
                .ToArray());
    }

    private static RemoteTimelineDto BuildTimeline(DateTime nowLocal, DaySchedule schedule)
    {
        var now = TimeOnly.FromDateTime(nowLocal);
        var periods = schedule.Periods.OrderBy(p => p.Start).ToArray();
        var current = periods.FirstOrDefault(p => p.Start <= now && now < p.End);
        if (current is not null)
        {
            return new RemoteTimelineDto(
                "inPeriod", current.Number, current.Start, current.End,
                DateOnly.FromDateTime(nowLocal).ToDateTime(current.End));
        }

        var next = periods.FirstOrDefault(p => now < p.Start);
        return next is null
            ? new RemoteTimelineDto("none", null, null, null, null)
            : new RemoteTimelineDto(
                "untilStart", next.Number, next.Start, next.End,
                DateOnly.FromDateTime(nowLocal).ToDateTime(next.Start));
    }

    private static bool IsProblemLog(string line) =>
        line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase);
}

public sealed record RemoteDiagnosticsDto(
    DateTime ServerLocalTime,
    RemoteTimelineDto Timeline,
    IReadOnlyList<RemoteHealthDto> ActiveHealth,
    IReadOnlyList<string> RecentIssues);

public sealed record RemoteTimelineDto(
    string State,
    int? PeriodNumber,
    TimeOnly? Start,
    TimeOnly? End,
    DateTime? TargetLocal);

public sealed record RemoteHealthDto(
    string Kind,
    string Severity,
    string Message,
    DateTime TimestampUtc);
