using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>Code-owned fixed schedule; persisted legacy timetable values are intentionally ignored.</summary>
public sealed class PeriodScheduleStore : IPeriodScheduleStore
{
    public DaySchedule ResolveForDate(DateOnly date) => FixedSchoolSchedule.ForDate(date);
}
