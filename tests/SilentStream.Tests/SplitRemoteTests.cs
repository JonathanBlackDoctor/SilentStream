using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class SplitRemoteTests
{
    private static readonly DateOnly Date = new(2026, 7, 13);
    private static readonly DateTime Now = At(9, 30);

    private readonly FakeSplitService _svc = new();
    private readonly FakeSchedule _schedule = new();
    private readonly PeriodsConfig _config = new();

    private SplitRemote.SplitsDto Build() =>
        SplitRemote.BuildDto(_svc, _schedule, _config, "m111", Now);

    private static DateTime At(int hour, int minute) => Date.ToDateTime(new TimeOnly(hour, minute));

    private static PendingSplit Split(
        string id, int[] periods, string status = PendingSplitStatus.Pending,
        DateTime? autoApproveAt = null, DateTime? executeAt = null) =>
        new(id, Date, periods, At(8, 25), At(9, 25), @"C:\rec\session.mp4", At(8, 20),
            status, At(9, 25), autoApproveAt, executeAt, null, null, null, null);

    private void ScheduleTwoPeriods() =>
        _schedule.Day = new DaySchedule(
        [
            new SchoolPeriod(1, new TimeOnly(8, 25), new TimeOnly(9, 25)),
            new SchoolPeriod(2, new TimeOnly(9, 25), new TimeOnly(10, 25)),
        ]);

    [Fact]
    public void Pending_dto_carries_label_title_countdown_and_merge_gate()
    {
        ScheduleTwoPeriods();
        _svc.Items.Add(Split("a", [1], autoApproveAt: At(9, 40)));

        var dto = Build();

        var p = Assert.Single(dto.Pending);
        Assert.Equal("1교시", p.Label);
        Assert.Equal("2026-07-13T08:25:00", p.Start);
        Assert.Equal("2026-07-13T09:25:00", p.End);
        Assert.Equal("[영상] m111 | 2026-07-13 | 1교시", p.ProposedTitle);
        Assert.Equal(600, p.AutoApproveInSec); // 09:30 → 09:40, server-computed
        Assert.True(p.CanMerge);               // period 2 ends 10:25, still ahead
        Assert.True(dto.RequireApproval);
        Assert.Equal(15, dto.AutoApproveMinutes);
    }

    [Fact]
    public void Merged_pending_uses_the_range_label()
    {
        _svc.Items.Add(Split("a", [1, 2]));

        var p = Assert.Single(Build().Pending);

        Assert.Equal("1~2교시", p.Label);
        Assert.Equal("[영상] m111 | 2026-07-13 | 1~2교시", p.ProposedTitle);
    }

    [Fact]
    public void Infinite_wait_has_no_countdown()
    {
        _svc.Items.Add(Split("a", [1], autoApproveAt: null));

        Assert.Null(Assert.Single(Build().Pending).AutoApproveInSec);
    }

    [Theory]
    [InlineData(true)]  // an open chain blocks another merge
    [InlineData(false)] // no next period today blocks merge
    public void Merge_gate_closes_when_unavailable(bool chainOpen)
    {
        if (chainOpen)
        {
            ScheduleTwoPeriods();
            _svc.Chain = new MergeChain(Date, [3], At(7, 0), At(8, 0), @"C:\rec\s.mp4", At(7, 0));
        }
        // else: schedule stays empty — nothing after period 1
        _svc.Items.Add(Split("a", [1]));

        Assert.False(Assert.Single(Build().Pending).CanMerge);
    }

    [Fact]
    public void Merge_gate_closes_once_the_next_period_ended()
    {
        _schedule.Day = new DaySchedule(
        [
            new SchoolPeriod(1, new TimeOnly(8, 25), new TimeOnly(9, 25)),
            new SchoolPeriod(2, new TimeOnly(8, 25), new TimeOnly(9, 25)), // ended at 09:25 < now
        ]);
        _svc.Items.Add(Split("a", [1]));

        Assert.False(Assert.Single(Build().Pending).CanMerge);
    }

    [Fact]
    public void Open_chain_is_reported_with_its_next_period()
    {
        ScheduleTwoPeriods();
        _svc.Chain = new MergeChain(Date, [1], At(8, 25), At(9, 25), @"C:\rec\s.mp4", At(8, 20));

        var chain = Build().Chain;

        Assert.NotNull(chain);
        Assert.Equal("1교시", chain.Label);
        Assert.Equal(2, chain.NextPeriod);
        Assert.Equal("2026-07-13T08:25:00", chain.Start);
    }

    [Fact]
    public void Terminal_splits_land_in_recent_newest_first()
    {
        _svc.Items.Add(Split("old", [1], PendingSplitStatus.Done));
        _svc.Items.Add(Split("new", [2], PendingSplitStatus.Skipped));
        _svc.Items.Add(Split("live", [3]));

        var dto = Build();

        Assert.Equal("live", Assert.Single(dto.Pending).Id);
        Assert.Equal(["new", "old"], dto.Recent.Select(r => r.Id));
    }

    [Fact]
    public void Approved_split_stays_active_and_reports_its_scheduled_cut()
    {
        _svc.Items.Add(Split("a", [1], PendingSplitStatus.Approved, executeAt: At(9, 40)));

        var p = Assert.Single(Build().Pending);

        Assert.Equal(PendingSplitStatus.Approved, p.Status);
        Assert.Equal("2026-07-13T09:40:00", p.ExecuteAt);
        Assert.Null(p.AutoApproveInSec); // countdown is a pending-only concept
        Assert.False(p.CanMerge);
    }

    [Fact]
    public void Approve_parses_short_times_against_the_split_date()
    {
        _svc.Items.Add(Split("a", [1]));

        var result = SplitRemote.Approve(
            _svc, "a", new SplitRemote.ApproveRequest("08:30", "09:40"), Build);

        Assert.True(result.Ok);
        Assert.Equal("cut", result.Applied);
        Assert.Equal(("a", At(8, 30), At(9, 40)), _svc.ApproveCall);
    }

    [Fact]
    public void Approve_accepts_iso_local_timestamps()
    {
        _svc.Items.Add(Split("a", [1]));

        var result = SplitRemote.Approve(
            _svc, "a", new SplitRemote.ApproveRequest(null, "2026-07-13T09:41:30"), Build);

        Assert.True(result.Ok);
        Assert.Equal(("a", (DateTime?)null, Date.ToDateTime(new TimeOnly(9, 41, 30))), _svc.ApproveCall);
    }

    [Fact]
    public void Approve_rejects_garbage_times_without_touching_the_service()
    {
        _svc.Items.Add(Split("a", [1]));

        var result = SplitRemote.Approve(
            _svc, "a", new SplitRemote.ApproveRequest(null, "9시40분"), Build);

        Assert.False(result.Ok);
        Assert.Contains("종료 시각 형식", result.Error);
        Assert.Null(_svc.ApproveCall);
    }

    [Fact]
    public void Approve_reports_an_unknown_id()
    {
        var result = SplitRemote.Approve(_svc, "ghost", null, Build);

        Assert.False(result.Ok);
        Assert.Equal("승인 대기 항목을 찾을 수 없습니다.", result.Error);
        Assert.Null(_svc.ApproveCall);
    }

    [Fact]
    public void Action_wrappers_pass_service_outcomes_through()
    {
        _svc.NextOutcome = new SplitActionOutcome(false, "오늘 남은 다음 교시가 없어 연강할 수 없습니다.");

        var result = SplitRemote.Merge(_svc, "a", Build);

        Assert.False(result.Ok);
        Assert.Equal("오늘 남은 다음 교시가 없어 연강할 수 없습니다.", result.Error);
    }

    private sealed class FakeSplitService : ISplitApprovalService
    {
        public List<PendingSplit> Items { get; } = [];
        public MergeChain? Chain;
        public (string Id, DateTime? Start, DateTime? End)? ApproveCall;
        public SplitActionOutcome NextOutcome = new(true, null, "cut");

        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<PendingSplit> Snapshot() => Items.ToList();
        public MergeChain? OpenChain() => Chain;
        public void Start(CancellationToken ct) { }
        public void OnPeriodEnded(PeriodBoundary boundary) { }
        public SplitActionOutcome Approve(string id, DateTime? startLocal, DateTime? endLocal)
        {
            ApproveCall = (id, startLocal, endLocal);
            return NextOutcome;
        }
        public SplitActionOutcome Merge(string id) => NextOutcome;
        public SplitActionOutcome CancelMerge() => NextOutcome;
        public SplitActionOutcome Skip(string id) => NextOutcome;
    }

    private sealed class FakeSchedule : IPeriodScheduleStore
    {
        public DaySchedule Day = DaySchedule.Empty;
        public DaySchedule GetWeekdayDefault(DayOfWeek day) => Day;
        public void SetWeekdayDefault(DayOfWeek day, DaySchedule schedule) { }
        public DaySchedule? GetOverride(DateOnly date) => null;
        public void SetOverride(DateOnly date, DaySchedule schedule) { }
        public void ClearOverride(DateOnly date) { }
        public DaySchedule ResolveForDate(DateOnly date) => Day;
    }
}
