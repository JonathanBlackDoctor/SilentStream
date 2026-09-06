using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Resolves the immutable weekday timetable. There are no user-editable defaults or date
/// exceptions; weekends always resolve to an empty schedule.
/// </summary>
public interface IPeriodScheduleStore
{
    /// <summary>Returns nine fixed slots on weekdays and none on weekends.</summary>
    DaySchedule ResolveForDate(DateOnly date);
}
