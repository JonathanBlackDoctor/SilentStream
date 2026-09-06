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

    private static PeriodScheduleStore CreateStore() => new();

    [Fact]
    public void Weekday_uses_nine_fixed_slots_including_lunch()
    {
        var store = CreateStore();
        var loaded = store.ResolveForDate(new DateOnly(2026, 6, 15));
        Assert.Equal(9, loaded.Periods.Count);
        Assert.Equal(new TimeOnly(8, 25), loaded.Periods[0].Start);
        Assert.Equal(new TimeOnly(17, 25), loaded.Periods[^1].End);
        var lunch = loaded.Periods[4];
        Assert.Equal("lunch", lunch.ResolvedSlotId);
        Assert.Equal("점심시간", lunch.ResolvedSlotLabel);
        Assert.Equal(ScheduleSlotKinds.Lunch, lunch.ResolvedSlotKind);
        Assert.Equal(new TimeOnly(12, 25), lunch.Start);
        Assert.Equal(new TimeOnly(13, 25), lunch.End);
    }

    [Fact]
    public void Persisted_rows_are_ignored()
    {
        var store = CreateStore();
        var date = new DateOnly(2026, 6, 15);
        Assert.Equal(9, store.ResolveForDate(date).Periods.Count);
    }

    [Theory]
    [InlineData("2026-06-20")]
    [InlineData("2026-06-21")]
    public void Weekend_resolves_to_empty(string value)
    {
        var resolved = CreateStore().ResolveForDate(DateOnly.Parse(value));
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

    [Fact]
    public void Merged_periods_render_first_to_last()
    {
        var result = TitleTemplater.Expand(
            "[{호실}] {교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 14), new[] { 1, 2 }, "201호");

        Assert.Equal("[201호] 1~2교시 - 2026-06-14", result);
    }

    [Fact]
    public void Merged_padded_period_token_pads_each_end()
    {
        Assert.Equal("01~03교시",
            TitleTemplater.Expand("{교시:00}교시", new DateTime(2026, 6, 14), new[] { 1, 2, 3 }));
    }

    [Fact]
    public void Single_element_merge_matches_the_int_overload()
    {
        Assert.Equal(
            TitleTemplater.Expand("{교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 14), 1),
            TitleTemplater.Expand("{교시}교시 - {yyyy-MM-dd}", new DateTime(2026, 6, 14), new[] { 1 }));
    }

    [Fact]
    public void Period_label_file_base_formats_single_and_merged_runs()
    {
        Assert.Equal("1교시", PeriodLabel.FileBase([1]));
        Assert.Equal("1~2교시", PeriodLabel.FileBase([1, 2]));
        Assert.Equal("5~8교시", PeriodLabel.FileBase([5, 6, 7, 8]));
    }
}
