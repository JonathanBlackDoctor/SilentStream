using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class RemoteDiagnosticsTests
{
    [Fact]
    public void Current_period_counts_down_to_its_end()
    {
        var now = new DateTime(2026, 7, 13, 9, 20, 0);
        var schedule = new DaySchedule([new SchoolPeriod(1, new TimeOnly(9, 0), new TimeOnly(9, 50))]);

        var result = RemoteDiagnostics.Build(now, schedule, [], []);

        Assert.Equal("inPeriod", result.Timeline.State);
        Assert.Equal(1, result.Timeline.PeriodNumber);
        Assert.Equal(new DateTime(2026, 7, 13, 9, 50, 0), result.Timeline.TargetLocal);
    }

    [Fact]
    public void Break_counts_down_to_the_next_period_start()
    {
        var now = new DateTime(2026, 7, 13, 10, 0, 0);
        var schedule = new DaySchedule(
        [
            new SchoolPeriod(1, new TimeOnly(9, 0), new TimeOnly(9, 50)),
            new SchoolPeriod(2, new TimeOnly(10, 10), new TimeOnly(11, 0))
        ]);

        var result = RemoteDiagnostics.Build(now, schedule, [], []);

        Assert.Equal("untilStart", result.Timeline.State);
        Assert.Equal(2, result.Timeline.PeriodNumber);
        Assert.Equal(new DateTime(2026, 7, 13, 10, 10, 0), result.Timeline.TargetLocal);
    }

    [Fact]
    public void Includes_only_recent_warning_and_error_logs_in_newest_first_order()
    {
        var logs = new[] { "[INFO] ready", "[WARN] first", "[ERROR] second", "[DEBUG] ignored" };
        var health = new[]
        {
            new HealthEvent(HealthEventKind.RtmpDown, HealthSeverity.Critical, true,
                "송출 연결을 확인하세요.", null, "201", DateTime.UtcNow)
        };

        var result = RemoteDiagnostics.Build(DateTime.Now, DaySchedule.Empty, health, logs);

        Assert.Equal(["[ERROR] second", "[WARN] first"], result.RecentIssues);
        var active = Assert.Single(result.ActiveHealth);
        Assert.Equal("RtmpDown", active.Kind);
        Assert.Equal("Critical", active.Severity);
    }
}
