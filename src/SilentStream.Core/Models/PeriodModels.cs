namespace SilentStream.Core.Models;

/// <summary>
/// A single school period (교시): its number and the local wall-clock window it occupies.
/// Times are <see cref="TimeOnly"/> so a schedule is reusable across dates (확장계획서 §5).
/// </summary>
/// <param name="Number">1-based period number (1교시, 2교시 …).</param>
/// <param name="Start">Local start time (e.g. 09:00:00).</param>
/// <param name="End">Local end time (e.g. 09:50:00).</param>
public sealed record SchoolPeriod(int Number, TimeOnly Start, TimeOnly End);

/// <summary>
/// An ordered set of periods for one day (a weekday default or a date override). See 확장계획서 §5.
/// </summary>
/// <param name="Periods">Periods in display/number order.</param>
public sealed record DaySchedule(IReadOnlyList<SchoolPeriod> Periods)
{
    /// <summary>An empty schedule (no periods → scheduler fires nothing).</summary>
    public static DaySchedule Empty { get; } = new(Array.Empty<SchoolPeriod>());
}

/// <summary>
/// A concrete period boundary resolved against a calendar date: the period number plus
/// its absolute local start/end timestamps. Emitted by <c>IPeriodScheduler</c> and consumed
/// by the VOD cut + upload pipeline (확장계획서 §5).
/// </summary>
/// <param name="Date">The calendar date this boundary belongs to.</param>
/// <param name="PeriodNumber">1-based period number.</param>
/// <param name="StartLocal">Absolute local start timestamp.</param>
/// <param name="EndLocal">Absolute local end timestamp.</param>
public sealed record PeriodBoundary(
    DateOnly Date,
    int PeriodNumber,
    DateTime StartLocal,
    DateTime EndLocal);

/// <summary>
/// Formats a (possibly merged, 연강) run of period numbers for titles and file names:
/// [1] → "1", [1,2,3] → "1~3" (first~last). Shared by the {교시} title token and the VOD
/// output file name so the two never drift apart.
/// </summary>
public static class PeriodLabel
{
    /// <summary>The bare {교시} token value; <paramref name="numericFormat"/> pads each end ("01~03").</summary>
    public static string Token(IReadOnlyList<int> periods, string? numericFormat = null)
    {
        var first = Format(periods[0], numericFormat);
        return periods.Count == 1 ? first : $"{first}~{Format(periods[^1], numericFormat)}";
    }

    /// <summary>File-name base, e.g. "1교시" / "1~2교시" ('~' is a valid Windows filename char).</summary>
    public static string FileBase(IReadOnlyList<int> periods) => Token(periods) + "교시";

    private static string Format(int n, string? numericFormat) =>
        numericFormat is null ? n.ToString() : n.ToString(numericFormat);
}
