using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class SplitApprovalServiceTests : IDisposable
{
    private static readonly DateOnly Date = new(2026, 7, 13); // a Monday

    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-split-").FullName;
    private readonly ConfigStore _configStore;
    private readonly PeriodScheduleStore _scheduleStore;
    private readonly FakeVod _vod = new();
    private readonly FakeQueue _queue = new();
    private readonly FakeSession _session = new();
    private readonly FakeAudioExport _audio = new();
    private readonly FakeAssetCatalog _assets = new();
    private readonly VirtualClock _clock = new(At(9, 25));
    private readonly CancellationTokenSource _cts = new();
    private int _idSeq;

    public SplitApprovalServiceTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        // Default to manual-only in tests: with the virtual clock, any auto-approve deadline is
        // reached the moment the worker sleeps, which would race the manual actions under test.
        // Auto-approve behaviour gets its own tests that opt back in.
        config.Periods.AutoApproveMinutes = null;
        _configStore.Save(config);
        _scheduleStore = new PeriodScheduleStore(_configStore);
        _session.Current = new RecordingSession(Path.Combine(_dir, "session.mp4"), At(8, 20));
    }

    private SplitApprovalService Create() =>
        new(_vod, _queue, _configStore, _session, _scheduleStore, _audio, _assets, new LogService(),
            Path.Combine(_dir, "pending_splits.json"),
            () => _clock.Now, _clock.Delay, () => $"id-{++_idSeq}");

    private static DateTime At(int hour, int minute) => Date.ToDateTime(new TimeOnly(hour, minute));

    private static PeriodBoundary B(int n, DateTime start, DateTime end) => new(Date, n, start, end);

    private void SetTodaySchedule(params (int no, int sh, int sm, int eh, int em)[] rows) =>
        _scheduleStore.SetOverride(Date, new DaySchedule(rows
            .Select(r => new SchoolPeriod(r.no, new TimeOnly(r.sh, r.sm), new TimeOnly(r.eh, r.em)))
            .ToList()));

    [Fact]
    public void Period_end_creates_a_pending_split_with_a_session_snapshot_instead_of_cutting()
    {
        var svc = Create();
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var split = Assert.Single(svc.Snapshot());
        Assert.Equal(PendingSplitStatus.Pending, split.Status);
        Assert.Equal([1], split.Periods);
        Assert.Equal(At(8, 25), split.StartLocal);
        Assert.Equal(At(9, 25), split.EndLocal);
        Assert.Equal(_session.Current!.FilePath, split.SessionFilePath);
        Assert.Equal(_session.Current.StartLocal, split.SessionStartLocal);
        Assert.Null(split.AutoApproveAtLocal); // 무한 대기 설정
        Assert.Empty(_vod.RangeCalls);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void No_recording_session_creates_no_pending()
    {
        _session.Current = null;
        var svc = Create();
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Boundary_entirely_before_recording_start_creates_no_pending()
    {
        _session.Current = new RecordingSession(Path.Combine(_dir, "session.mp4"), At(9, 30));
        var svc = Create();
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25))); // ended before recording began

        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public async Task Approve_unadjusted_cuts_the_default_window_and_enqueues_with_title()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        var id = svc.Snapshot()[0].Id;

        var outcome = svc.Approve(id, null, null);

        Assert.True(outcome.Ok);
        Assert.Equal("cut", outcome.Applied);
        await WaitUntilAsync(() => svc.Snapshot()[0].Status == PendingSplitStatus.Done);

        var call = Assert.Single(_vod.RangeCalls);
        Assert.Equal((At(8, 25), At(9, 25), "1교시"), (call.Start, call.End, call.Label));
        Assert.Equal(_session.Current!.FilePath, call.Session.FilePath);
        var job = Assert.Single(_queue.Jobs);
        Assert.Equal("1교시 - 2026-07-13", job.Title);
        Assert.Equal(1, job.PeriodNumber);
        Assert.Equal("1교시 - 2026-07-13", svc.Snapshot()[0].Title);
        // The durable-download step (audio export + asset catalog) mirrors the immediate path.
        var asset = Assert.Single(_assets.Assets);
        Assert.Equal(job.Id, asset.Id);
        Assert.Equal(_audio.ResultPath, asset.AudioPath);
    }

    [Fact]
    public async Task Approve_with_adjusted_past_end_cuts_the_adjusted_window()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Approve(svc.Snapshot()[0].Id, null, At(9, 15)); // 수업이 당겨짐

        Assert.True(outcome.Ok);
        await WaitUntilAsync(() => _vod.RangeCalls.Count >= 1);
        Assert.Equal(At(9, 15), Assert.Single(_vod.RangeCalls).End);
    }

    [Fact]
    public async Task Approve_with_future_end_defers_the_cut_until_that_instant()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Approve(svc.Snapshot()[0].Id, null, At(9, 40)); // 수업이 길어짐

        Assert.True(outcome.Ok);
        Assert.Equal("scheduled", outcome.Applied);
        // The worker's virtual-clock sleep advances time to 09:40, then the cut runs.
        await WaitUntilAsync(() => _vod.RangeCalls.Count >= 1);
        Assert.Equal(At(9, 40), Assert.Single(_vod.RangeCalls).End);
        Assert.True(_clock.Now >= At(9, 40));
    }

    [Theory]
    [InlineData(8, 30, "종료 시각은 시작보다 1분 이상 뒤여야 합니다.", 8, 30)] // end == start
    [InlineData(8, 10, "종료 시각은 시작보다 1분 이상 뒤여야 합니다.", 8, 25)] // end < start
    public void Approve_rejects_an_invalid_window(int eh, int em, string error, int sh, int sm)
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Approve(svc.Snapshot()[0].Id, At(sh, sm), At(eh, em));

        Assert.False(outcome.Ok);
        Assert.Equal(error, outcome.Error);
        Assert.Equal(PendingSplitStatus.Pending, svc.Snapshot()[0].Status); // untouched
    }

    [Fact]
    public void Approve_rejects_an_end_before_recording_start()
    {
        _session.Current = new RecordingSession(Path.Combine(_dir, "session.mp4"), At(9, 0));
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Approve(svc.Snapshot()[0].Id, null, At(8, 50));

        Assert.False(outcome.Ok);
        Assert.Contains("녹화 시작 이전", outcome.Error);
    }

    [Fact]
    public async Task Adjusted_end_shifts_the_next_contiguous_period_start()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        svc.Approve(svc.Snapshot()[0].Id, null, At(9, 15)); // boundary moved to 09:15
        await WaitUntilAsync(() => _vod.RangeCalls.Count >= 1);

        _clock.AdvanceTo(At(10, 25));
        svc.OnPeriodEnded(B(2, At(9, 25), At(10, 25))); // scheduled start == previous scheduled end

        var next = Assert.Single(svc.Snapshot(), s => s.Status == PendingSplitStatus.Pending);
        Assert.Equal(At(9, 15), next.StartLocal); // follows the moved boundary
        Assert.Equal(At(10, 25), next.EndLocal);
    }

    [Fact]
    public async Task Next_period_absorbed_by_a_long_extension_is_auto_skipped()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        svc.Approve(svc.Snapshot()[0].Id, null, At(10, 25)); // extended across all of period 2
        await WaitUntilAsync(() => _vod.RangeCalls.Count >= 1);

        svc.OnPeriodEnded(B(2, At(9, 25), At(10, 25)));

        var absorbed = Assert.Single(svc.Snapshot(), s => s.Periods.SequenceEqual(new[] { 2 }));
        Assert.Equal(PendingSplitStatus.Skipped, absorbed.Status);
        Assert.Equal("이전 교시 연장에 흡수됨", absorbed.FailReason);
    }

    [Fact]
    public async Task Merge_opens_a_chain_and_the_next_boundary_creates_a_merged_pending()
    {
        SetTodaySchedule((1, 8, 25, 9, 25), (2, 9, 25, 10, 25));
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Merge(svc.Snapshot()[0].Id); // 연강

        Assert.True(outcome.Ok);
        Assert.Empty(svc.Snapshot());
        Assert.NotNull(svc.OpenChain());

        _clock.AdvanceTo(At(10, 25));
        svc.OnPeriodEnded(B(2, At(9, 25), At(10, 25)));

        Assert.Null(svc.OpenChain());
        var merged = Assert.Single(svc.Snapshot());
        Assert.Equal([1, 2], merged.Periods);
        Assert.Equal(At(8, 25), merged.StartLocal);
        Assert.Equal(At(10, 25), merged.EndLocal);

        svc.Approve(merged.Id, null, null);
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1);
        Assert.Equal("1~2교시", Assert.Single(_vod.RangeCalls).Label);
        Assert.Equal("1~2교시 - 2026-07-13", Assert.Single(_queue.Jobs).Title);
    }

    [Fact]
    public void Merge_fails_without_a_remaining_period_today()
    {
        SetTodaySchedule((1, 8, 25, 9, 25)); // nothing after period 1
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Merge(svc.Snapshot()[0].Id);

        Assert.False(outcome.Ok);
        Assert.Equal("오늘 남은 다음 교시가 없어 연강할 수 없습니다.", outcome.Error);
    }

    [Fact]
    public void Merge_fails_when_the_next_period_already_ended()
    {
        SetTodaySchedule((1, 8, 25, 9, 25), (2, 9, 25, 10, 25));
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        _clock.AdvanceTo(At(10, 30)); // period 2 is over — its boundary already fired (or never will)
        var outcome = svc.Merge(svc.Snapshot()[0].Id);

        Assert.False(outcome.Ok);
        Assert.Equal("다음 교시가 이미 끝나 연강할 수 없습니다.", outcome.Error);
    }

    [Fact]
    public void Cancel_merge_restores_the_pre_merge_pending()
    {
        SetTodaySchedule((1, 8, 25, 9, 25), (2, 9, 25, 10, 25));
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        svc.Merge(svc.Snapshot()[0].Id);

        var outcome = svc.CancelMerge();

        Assert.True(outcome.Ok);
        Assert.Null(svc.OpenChain());
        var restored = Assert.Single(svc.Snapshot());
        Assert.Equal(PendingSplitStatus.Pending, restored.Status);
        Assert.Equal((At(8, 25), At(9, 25)), (restored.StartLocal, restored.EndLocal));
    }

    [Fact]
    public void Skip_discards_the_window_without_cutting()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        var outcome = svc.Skip(svc.Snapshot()[0].Id);

        Assert.True(outcome.Ok);
        Assert.Equal(PendingSplitStatus.Skipped, svc.Snapshot()[0].Status);
        Assert.Empty(_vod.RangeCalls);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void Second_action_on_the_same_split_loses_the_race()
    {
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        var id = svc.Snapshot()[0].Id;

        Assert.True(svc.Approve(id, null, null).Ok);
        var loser = svc.Skip(id);

        Assert.False(loser.Ok);
        Assert.Equal("이미 처리된 항목입니다.", loser.Error);
    }

    [Fact]
    public async Task Untouched_pending_auto_approves_at_the_default_window_after_the_deadline()
    {
        _configStore.Update(c => c.Periods.AutoApproveMinutes = 15);
        var svc = Create();
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        // The worker sleeps to the 09:40 deadline (virtual clock), auto-approves, then cuts.
        await WaitUntilAsync(() => _queue.Jobs.Count >= 1);
        Assert.Equal((At(8, 25), At(9, 25)), (_vod.RangeCalls[0].Start, _vod.RangeCalls[0].End));
        Assert.Equal(PendingSplitStatus.Done, svc.Snapshot()[0].Status);
        Assert.True(_clock.Now >= At(9, 40));
    }

    [Fact]
    public async Task Infinite_wait_keeps_the_pending_untouched()
    {
        var svc = Create(); // AutoApproveMinutes = null (ctor default for tests)
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        await Task.Delay(150); // real time: give the worker a chance to misbehave

        Assert.Equal(PendingSplitStatus.Pending, svc.Snapshot()[0].Status);
        Assert.Empty(_vod.RangeCalls);
    }

    [Fact]
    public async Task Failed_cut_surfaces_a_reason_instead_of_uploading()
    {
        _vod.ResultPath = null; // session file gone / extraction impossible
        var svc = Create();
        svc.Start(_cts.Token);
        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));

        svc.Approve(svc.Snapshot()[0].Id, null, null);
        await WaitUntilAsync(() => svc.Snapshot()[0].Status == PendingSplitStatus.Failed);

        Assert.Contains("컷 실패", svc.Snapshot()[0].FailReason);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public async Task State_survives_a_restart_and_an_approved_uncut_split_reruns()
    {
        var first = Create(); // never started: approve marks the cut owed, no worker to run it
        first.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        Assert.True(first.Approve(first.Snapshot()[0].Id, null, null).Ok);
        Assert.Empty(_vod.RangeCalls);

        var second = Create(); // same state file — simulates the app restarting
        second.Start(_cts.Token);

        await WaitUntilAsync(() => second.Snapshot().Any(s => s.Status == PendingSplitStatus.Done));
        var call = Assert.Single(_vod.RangeCalls);
        Assert.Equal((At(8, 25), At(9, 25)), (call.Start, call.End));
    }

    [Fact]
    public async Task Auto_approve_deadline_missed_during_downtime_fires_on_start()
    {
        _configStore.Update(c => c.Periods.AutoApproveMinutes = 15);
        var first = Create(); // never started
        first.OnPeriodEnded(B(1, At(8, 25), At(9, 25))); // deadline 09:40

        _clock.AdvanceTo(At(11, 0)); // the app was down past the deadline
        var second = Create();
        second.Start(_cts.Token);

        await WaitUntilAsync(() => second.Snapshot().Any(s => s.Status == PendingSplitStatus.Done));
        Assert.Equal(At(9, 25), Assert.Single(_vod.RangeCalls).End); // default boundary
    }

    [Fact]
    public void Stranded_merge_chain_materializes_as_a_pending_on_start()
    {
        SetTodaySchedule((1, 8, 25, 9, 25), (2, 9, 25, 10, 25));
        var first = Create(); // never started
        first.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        Assert.True(first.Merge(first.Snapshot()[0].Id).Ok);

        var second = Create(); // restart: the closing boundary will never re-fire
        second.Start(_cts.Token);

        Assert.Null(second.OpenChain());
        var restored = Assert.Single(second.Snapshot());
        Assert.Equal(PendingSplitStatus.Pending, restored.Status);
        Assert.Equal((At(8, 25), At(9, 25)), (restored.StartLocal, restored.EndLocal)); // pre-merge window
    }

    [Fact]
    public void Changed_fires_on_creation_and_actions()
    {
        var svc = Create();
        var events = 0;
        svc.Changed += (_, _) => Interlocked.Increment(ref events);
        svc.Start(_cts.Token);

        svc.OnPeriodEnded(B(1, At(8, 25), At(9, 25)));
        var afterCreate = events;
        svc.Skip(svc.Snapshot()[0].Id);

        Assert.True(afterCreate >= 1);
        Assert.True(events > afterCreate);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        // The virtual-clock worker observes cancellation asynchronously and can still have the
        // config file open for a few milliseconds. Wait briefly so test cleanup is not flaky on
        // Windows' exclusive file handles.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (true)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Virtual clock: a delay advances the clock and yields (see PeriodSchedulerTests).</summary>
    private sealed class VirtualClock(DateTime start)
    {
        private readonly object _lock = new();
        private DateTime _now = start;

        public DateTime Now { get { lock (_lock) { return _now; } } }

        public void AdvanceTo(DateTime t)
        {
            lock (_lock)
            {
                if (t > _now)
                {
                    _now = t;
                }
            }
        }

        public async Task Delay(TimeSpan span, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (span > TimeSpan.FromHours(6))
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // park
                return;
            }
            lock (_lock) { _now = _now.Add(span); }
            await Task.Yield();
        }
    }

    private sealed class FakeSession : IRecordingSessionInfo
    {
        public RecordingSession? Current { get; set; }
    }

    private sealed class FakeVod : IVodSegmentService
    {
        public string? ResultPath = "/vod/cut.mp4";
        private readonly List<(RecordingSession Session, DateTime Start, DateTime End, string Label)> _calls = [];
        public IReadOnlyList<(RecordingSession Session, DateTime Start, DateTime End, string Label)> RangeCalls
        {
            get { lock (_calls) { return _calls.ToList(); } }
        }
        public Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct) =>
            Task.FromResult<string?>(null); // the approval path must never use the Current-session cut
        public Task<string?> ExtractRangeAsync(
            RecordingSession session, DateTime startLocal, DateTime endLocal, string fileBaseLabel,
            CancellationToken ct)
        {
            lock (_calls) { _calls.Add((session, startLocal, endLocal, fileBaseLabel)); }
            return Task.FromResult(ResultPath);
        }
    }

    private sealed class FakeQueue : IUploadQueue
    {
        private readonly List<UploadJob> _jobs = [];
        public IReadOnlyList<UploadJob> Jobs { get { lock (_jobs) { return _jobs.ToList(); } } }
        public void Enqueue(UploadJob job) { lock (_jobs) { _jobs.Add(job); } }
        public IReadOnlyList<UploadJob> Snapshot() => Jobs;
        public void Start(CancellationToken ct) { }
    }

    private sealed class FakeAudioExport : ILocalAudioExportService
    {
        public string? ResultPath = "/assets/audio.m4a";
        public Task<string?> ExportAsync(string sourceVideoPath, string assetId, CancellationToken ct) =>
            Task.FromResult(ResultPath);
    }

    private sealed class FakeAssetCatalog : IPeriodAssetCatalog
    {
        private readonly List<PeriodAsset> _assets = [];
        public IReadOnlyList<PeriodAsset> Assets { get { lock (_assets) { return _assets.ToList(); } } }
        public PeriodAsset Upsert(PeriodAsset asset)
        {
            lock (_assets) { _assets.Add(asset); }
            return asset;
        }
        public IReadOnlyList<PeriodAsset> Snapshot() => Assets;
        public PeriodAsset? Find(string id) => Assets.SingleOrDefault(a => a.Id == id);
        public bool MarkUploaded(string id, string videoId) => false;
        public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null) => false;
    }
}
