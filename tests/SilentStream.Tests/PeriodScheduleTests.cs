using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;
using Xunit;

namespace SilentStream.Tests;

public class PeriodScheduleStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-period-").FullName;
    private readonly ConfigStore _configStore;

    public PeriodScheduleStoreTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault());
    }

    private PeriodScheduleStore CreateStore() => new(_configStore);

    private static DaySchedule Make(params (int no, string start, string end)[] rows) =>
        new(rows.Select(r => new SchoolPeriod(r.no, TimeOnly.Parse(r.start), TimeOnly.Parse(r.end))).ToList());

    [Fact]
    public void Weekday_default_round_trips_through_config()
    {
        var store = CreateStore();
        var monday = Make((1, "09:00:00", "09:50:00"), (2, "10:00:00", "10:50:00"));

        store.SetWeekdayDefault(DayOfWeek.Monday, monday);

        var loaded = CreateStore().GetWeekdayDefault(DayOfWeek.Monday);
        Assert.Equal(2, loaded.Periods.Count);
        Assert.Equal(new TimeOnly(9, 0), loaded.Periods[0].Start);
        Assert.Equal(new TimeOnly(10, 50), loaded.Periods[1].End);
    }

    [Fact]
    public void Resolve_prefers_override_then_falls_back_to_weekday_default()
    {
        var store = CreateStore();
        // 2026-06-15 is a Monday.
        var date = new DateOnly(2026, 6, 15);
        Assert.Equal(DayOfWeek.Monday, date.DayOfWeek);

        store.SetWeekdayDefault(DayOfWeek.Monday, Make((1, "09:00:00", "09:50:00")));
        store.SetOverride(date, Make((1, "09:00:00", "09:30:00"))); // short day

        // override wins
        var resolved = store.ResolveForDate(date);
        Assert.Single(resolved.Periods);
        Assert.Equal(new TimeOnly(9, 30), resolved.Periods[0].End);

        // clearing the override falls back to the weekday default
        store.ClearOverride(date);
        Assert.Null(store.GetOverride(date));
        Assert.Equal(new TimeOnly(9, 50), store.ResolveForDate(date).Periods[0].End);
    }

    [Fact]
    public void Unset_weekday_resolves_to_empty()
    {
        var resolved = CreateStore().ResolveForDate(new DateOnly(2026, 6, 16)); // Tuesday, nothing set
        Assert.Empty(resolved.Periods);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class PeriodTitleTemplaterTests
{
    [Fact]
    public void Period_token_and_date_token_combine()
    {
        var result = TitleTemplater.Expand(
            "{교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 14), 1);

        Assert.Equal("1교시 - 2026-06-14", result);
    }

    [Fact]
    public void Padded_period_token_uses_numeric_format()
    {
        Assert.Equal("03교시", TitleTemplater.Expand("{교시:00}교시", new DateTime(2026, 6, 14), 3));
    }

    [Fact]
    public void Period_overload_without_period_token_still_expands_dates()
    {
        Assert.Equal("2026 수업", TitleTemplater.Expand("{yyyy} 수업", new DateTime(2026, 6, 14), 5));
    }

    [Fact]
    public void Room_period_and_date_tokens_combine()
    {
        var result = TitleTemplater.Expand(
            "[{호실}] {교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 23), 1, "201호");

        Assert.Equal("[201호] 1교시 - 2026-06-23", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_room_collapses_in_period_overload(string? roomName)
    {
        var result = TitleTemplater.Expand(
            "[{호실}] {교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 23), 1, roomName);

        Assert.Equal("1교시 - 2026-06-23", result);
    }
}
