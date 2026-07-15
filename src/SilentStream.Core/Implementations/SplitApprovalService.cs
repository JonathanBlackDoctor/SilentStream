using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Approval-based period splitting (승인 기반 교시 분할). On each period end it records a pending
/// split — window, session snapshot, auto-approve deadline — instead of cutting immediately; the
/// operator approves/adjusts/merges/skips it from the phone, and a single worker executes approved
/// cuts through the existing <see cref="IVodSegmentService"/> → <see cref="IUploadQueue"/> path.
/// State persists to pending_splits.json (UploadQueue pattern: lock + tmp + atomic move) because
/// the scheduler never back-fires past boundaries — an unpersisted pending would be a lost 교시.
/// </summary>
public sealed class SplitApprovalService : ISplitApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IVodSegmentService _vod;
    private readonly IUploadQueue _queue;
    private readonly IConfigStore _configStore;
    private readonly IRecordingSessionInfo _sessionInfo;
    private readonly IPeriodScheduleStore _scheduleStore;
    private readonly ILocalAudioExportService _audioExport;
    private readonly IPeriodAssetCatalog _assets;
    private readonly ILogService _log;
    private readonly string _stateFile;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string> _idFactory;
    private readonly int _maxTerminalRetained;

    private readonly object _gate = new();
    private SplitState? _state;
    private Task? _worker;
    private volatile TaskCompletionSource _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler? Changed;

    public SplitApprovalService(
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        IRecordingSessionInfo sessionInfo,
        IPeriodScheduleStore scheduleStore,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log)
        : this(vod, queue, configStore, sessionInfo, scheduleStore, audioExport, assets, log,
            AppPaths.PendingSplitsFile, () => DateTime.Now, Task.Delay,
            () => Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>Test seam: redirect the state file, clock, delay, and id factory.</summary>
    public SplitApprovalService(
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        IRecordingSessionInfo sessionInfo,
        IPeriodScheduleStore scheduleStore,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log,
        string stateFile,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<string> idFactory,
        int maxTerminalRetained = 30)
    {
        _vod = vod;
        _queue = queue;
        _configStore = configStore;
        _sessionInfo = sessionInfo;
        _scheduleStore = scheduleStore;
        _audioExport = audioExport;
        _assets = assets;
        _log = log;
        _stateFile = stateFile;
        _now = now;
        _delay = delay;
        _idFactory = idFactory;
        _maxTerminalRetained = maxTerminalRetained;
    }

    public IReadOnlyList<PendingSplit> Snapshot()
    {
        lock (_gate)
        {
            return LoadLocked().Splits.ToList();
        }
    }

    public MergeChain? OpenChain()
    {
        lock (_gate)
        {
            return LoadLocked().Chain;
        }
    }

    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_worker is not null)
            {
                return; // idempotent
            }

            // Crash recovery: an open 연강 chain means the app died (or was closed) before the next
            // boundary arrived. The scheduler won't back-fire it, so materialise the chain as a
            // pending split with its pre-merge window — the operator can still adjust and approve.
            var state = LoadLocked();
            if (state.Chain is not null)
            {
                var chain = state.Chain;
                state.Chain = null;
                state.Splits.Add(NewPending(
                    chain.Date, chain.Periods, chain.StartLocal, chain.FallbackEndLocal,
                    chain.SessionFilePath, chain.SessionStartLocal));
                _log.Warn($"{PeriodLabel.FileBase(chain.Periods)} 연강이 다음 교시 없이 남아 있어 " +
                          "병합 전 구간으로 승인 대기에 복원했습니다.");
            }
            PruneTerminalLocked();
            PersistLocked();
            _worker = WorkerAsync(ct);
        }
        RaiseChanged();
    }

    public void OnPeriodEnded(PeriodBoundary boundary)
    {
        string? info = null;
        lock (_gate)
        {
            var state = LoadLocked();

            if (state.Chain is not null && state.Chain.Date == boundary.Date)
            {
                // Close the open 연강 chain: this boundary's end completes the merged window. The
                // chain's session snapshot anchors the cut (best-effort across a mid-window restart).
                var chain = state.Chain;
                state.Chain = null;
                var periods = chain.Periods.Append(boundary.PeriodNumber).ToList();
                var merged = NewPending(
                    boundary.Date, periods, chain.StartLocal, boundary.EndLocal,
                    chain.SessionFilePath, chain.SessionStartLocal);
                state.Splits.Add(merged);
                info = $"{PeriodLabel.FileBase(periods)} 연강 승인 대기 생성 " +
                       $"({merged.StartLocal:HH:mm}~{merged.EndLocal:HH:mm}).";
            }
            else
            {
                if (state.Chain is not null)
                {
                    // A stale chain from another date can never be closed by today's boundaries.
                    var stale = state.Chain;
                    state.Chain = null;
                    state.Splits.Add(NewPending(
                        stale.Date, stale.Periods, stale.StartLocal, stale.FallbackEndLocal,
                        stale.SessionFilePath, stale.SessionStartLocal));
                    _log.Warn($"{PeriodLabel.FileBase(stale.Periods)} 연강이 날짜를 넘겨 " +
                              "병합 전 구간으로 승인 대기에 복원했습니다.");
                }

                var session = _sessionInfo.Current;
                if (session is null)
                {
                    _log.Warn($"{boundary.PeriodNumber}교시 승인 대기 생략: 진행 중인 세션 녹화가 없습니다.");
                    PersistLocked();
                    return;
                }
                if (boundary.EndLocal <= session.StartLocal)
                {
                    _log.Warn($"{boundary.PeriodNumber}교시 승인 대기 생략: 녹화 시작 전에 끝난 교시입니다.");
                    PersistLocked();
                    return;
                }

                // Boundary-moved semantic: when the previous approval adjusted an end that was
                // contiguous with this period's scheduled start, this window starts there instead.
                var start = boundary.StartLocal;
                var last = state.LastApproved;
                if (last is not null && last.Date == boundary.Date &&
                    last.ScheduledEndLocal == boundary.StartLocal)
                {
                    start = last.ResolvedEndLocal;
                }

                if (start >= boundary.EndLocal)
                {
                    // Fully absorbed by the previous period's extension — keep a terminal record so
                    // the phone history explains why this period has no VOD of its own.
                    state.Splits.Add(NewPending(
                            boundary.Date, [boundary.PeriodNumber], boundary.StartLocal,
                            boundary.EndLocal, session.FilePath, session.StartLocal)
                        with
                        { Status = PendingSplitStatus.Skipped, FailReason = "이전 교시 연장에 흡수됨" });
                    info = $"{boundary.PeriodNumber}교시는 이전 교시 연장에 흡수되어 건너뜁니다.";
                }
                else
                {
                    var pending = NewPending(
                        boundary.Date, [boundary.PeriodNumber], start, boundary.EndLocal,
                        session.FilePath, session.StartLocal);
                    state.Splits.Add(pending);
                    info = $"{boundary.PeriodNumber}교시 승인 대기 생성 " +
                           $"({pending.StartLocal:HH:mm}~{pending.EndLocal:HH:mm}, " +
                           $"{(pending.AutoApproveAtLocal is { } at ? $"자동 승인 {at:HH:mm}" : "수동 승인 대기")}).";
                }
            }
            PersistLocked();
        }

        if (info is not null)
        {
            _log.Info(info);
        }
        _signal.TrySetResult();
        RaiseChanged();
    }

    public SplitActionOutcome Approve(string id, DateTime? startLocal, DateTime? endLocal)
    {
        SplitActionOutcome outcome;
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Splits.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return new SplitActionOutcome(false, "승인 대기 항목을 찾을 수 없습니다.");
            }
            var item = state.Splits[index];
            if (item.Status != PendingSplitStatus.Pending)
            {
                // Manual vs auto-approve race: exactly one wins inside this lock.
                return new SplitActionOutcome(false, "이미 처리된 항목입니다.");
            }

            var start = startLocal ?? item.StartLocal;
            var end = endLocal ?? item.EndLocal;
            if (DateOnly.FromDateTime(start) != item.Date || DateOnly.FromDateTime(end) != item.Date)
            {
                return new SplitActionOutcome(false, "시각은 해당 날짜 안에 있어야 합니다.");
            }
            if (end - start < TimeSpan.FromMinutes(1))
            {
                return new SplitActionOutcome(false, "종료 시각은 시작보다 1분 이상 뒤여야 합니다.");
            }
            if (end <= item.SessionStartLocal)
            {
                return new SplitActionOutcome(false, "종료 시각이 녹화 시작 이전이라 추출할 구간이 없습니다.");
            }

            var now = _now();
            state.Splits[index] = item with
            {
                Status = PendingSplitStatus.Approved,
                ResolvedStartLocal = start,
                ResolvedEndLocal = end,
                ExecuteAtLocal = end > now ? end : null
            };
            // Remember the resolved end so the next contiguous period's window starts there.
            state.LastApproved = new LastApproval(item.Date, item.EndLocal, end);
            PersistLocked();
            outcome = new SplitActionOutcome(true, null, end > now ? "scheduled" : "cut");
        }
        _signal.TrySetResult();
        RaiseChanged();
        return outcome;
    }

    public SplitActionOutcome Merge(string id)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Splits.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return new SplitActionOutcome(false, "승인 대기 항목을 찾을 수 없습니다.");
            }
            var item = state.Splits[index];
            if (item.Status != PendingSplitStatus.Pending)
            {
                return new SplitActionOutcome(false, "이미 처리된 항목입니다.");
            }
            if (state.Chain is not null)
            {
                return new SplitActionOutcome(false, "이미 진행 중인 연강이 있습니다.");
            }

            var next = NextScheduledPeriod(item.Date, item.Periods[^1]);
            if (next is null)
            {
                return new SplitActionOutcome(false, "오늘 남은 다음 교시가 없어 연강할 수 없습니다.");
            }
            if (_now() >= item.Date.ToDateTime(next.End))
            {
                return new SplitActionOutcome(false, "다음 교시가 이미 끝나 연강할 수 없습니다.");
            }

            state.Chain = new MergeChain(
                item.Date, item.Periods, item.StartLocal, item.EndLocal,
                item.SessionFilePath, item.SessionStartLocal);
            state.Splits.RemoveAt(index);
            PersistLocked();
            _log.Info($"{PeriodLabel.FileBase(item.Periods)}를 {next.Number}교시와 연강으로 병합 대기합니다.");
        }
        RaiseChanged();
        return new SplitActionOutcome(true, null);
    }

    public SplitActionOutcome CancelMerge()
    {
        lock (_gate)
        {
            var state = LoadLocked();
            if (state.Chain is null)
            {
                return new SplitActionOutcome(false, "진행 중인 연강이 없습니다.");
            }
            var chain = state.Chain;
            state.Chain = null;
            state.Splits.Add(NewPending(
                chain.Date, chain.Periods, chain.StartLocal, chain.FallbackEndLocal,
                chain.SessionFilePath, chain.SessionStartLocal));
            PersistLocked();
            _log.Info($"{PeriodLabel.FileBase(chain.Periods)} 연강을 취소하고 승인 대기로 되돌렸습니다.");
        }
        _signal.TrySetResult();
        RaiseChanged();
        return new SplitActionOutcome(true, null);
    }

    public SplitActionOutcome Skip(string id)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Splits.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return new SplitActionOutcome(false, "승인 대기 항목을 찾을 수 없습니다.");
            }
            var item = state.Splits[index];
            if (item.Status != PendingSplitStatus.Pending)
            {
                return new SplitActionOutcome(false, "이미 처리된 항목입니다.");
            }
            state.Splits[index] = item with { Status = PendingSplitStatus.Skipped };
            PersistLocked();
            _log.Info($"{PeriodLabel.FileBase(item.Periods)} VOD를 건너뜁니다(운영자 선택).");
        }
        RaiseChanged();
        return new SplitActionOutcome(true, null);
    }

    // ---- worker ----

    private async Task WorkerAsync(CancellationToken ct)
    {
        _log.Info("교시 분할 승인 워커를 시작했습니다.");
        while (!ct.IsCancellationRequested)
        {
            PendingSplit? cut = null;
            PendingSplit? autoApprove = null;
            DateTime? nextWake = null;
            var now = _now();

            lock (_gate)
            {
                foreach (var s in LoadLocked().Splits)
                {
                    if (s.Status == PendingSplitStatus.Approved)
                    {
                        var at = s.ExecuteAtLocal ?? DateTime.MinValue;
                        if (at <= now)
                        {
                            cut = s;
                            break;
                        }
                        nextWake = Earlier(nextWake, at);
                    }
                    else if (s.Status == PendingSplitStatus.Pending && s.AutoApproveAtLocal is { } deadline)
                    {
                        if (deadline <= now)
                        {
                            autoApprove ??= s;
                        }
                        else
                        {
                            nextWake = Earlier(nextWake, deadline);
                        }
                    }
                }
            }

            if (cut is not null)
            {
                await ExecuteCutAsync(cut, ct).ConfigureAwait(false);
                continue;
            }
            if (autoApprove is not null)
            {
                AutoApprove(autoApprove.Id);
                continue;
            }

            if (!await WaitAsync(nextWake, ct).ConfigureAwait(false))
            {
                return; // cancelled
            }
        }
    }

    private void AutoApprove(string id)
    {
        var applied = false;
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Splits.FindIndex(s => s.Id == id);
            if (index >= 0 && state.Splits[index].Status == PendingSplitStatus.Pending)
            {
                var item = state.Splits[index];
                var now = _now();
                state.Splits[index] = item with
                {
                    Status = PendingSplitStatus.Approved,
                    ResolvedStartLocal = item.StartLocal,
                    ResolvedEndLocal = item.EndLocal,
                    ExecuteAtLocal = item.EndLocal > now ? item.EndLocal : null
                };
                state.LastApproved = new LastApproval(item.Date, item.EndLocal, item.EndLocal);
                PersistLocked();
                applied = true;
                _log.Info($"{PeriodLabel.FileBase(item.Periods)} 승인 시한 경과 — 기본 시각으로 자동 승인합니다.");
            }
        }
        if (applied)
        {
            RaiseChanged();
        }
    }

    private async Task ExecuteCutAsync(PendingSplit item, CancellationToken ct)
    {
        var start = item.ResolvedStartLocal ?? item.StartLocal;
        var end = item.ResolvedEndLocal ?? item.EndLocal;
        var label = PeriodLabel.FileBase(item.Periods);
        string status;
        string? title = null;
        string? failReason = null;

        try
        {
            var segments = (item.RecordingSegments ?? [])
                .Concat(_sessionInfo.GetSegments(start, end))
                .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.EndLocal).First())
                .OrderBy(s => s.StartLocal)
                .ToList();
            if (segments.Count == 0)
            {
                segments.Add(new RecordingSegment(item.SessionFilePath, item.SessionStartLocal, null));
            }
            var path = await _vod.ExtractRangeAsync(segments, start, end, label, ct)
                .ConfigureAwait(false);
            if (path is null)
            {
                status = PendingSplitStatus.Failed;
                failReason = "컷 실패(세션 파일 없음 또는 추출 불가) — 로그를 확인하세요.";
            }
            else
            {
                var config = _configStore.Load();
                title = TitleTemplater.Expand(
                    config.Periods.TitleTemplate, start, item.Periods,
                    TitleTemplater.ResolveRoomName(config));
                var jobId = _idFactory();

                // Mirror of VodCoordinator's immediate path: export downloadable audio from the
                // local cut BEFORE enqueueing — the upload worker deletes the mp4 after upload.
                // Both steps are additive; failing them must never sacrifice the VOD upload.
                try
                {
                    var audioPath = await _audioExport.ExportAsync(path, jobId, ct).ConfigureAwait(false);
                    _assets.Upsert(new PeriodAsset(
                        jobId, item.Date, item.Periods[0], title, AudioPath: audioPath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Error($"{label} 다운로드 자산 처리 실패", ex);
                }

                _queue.Enqueue(new UploadJob(
                    jobId, path, title, item.Date, item.Periods[0],
                    UploadJobStatus.Pending, 0, null));
                status = PendingSplitStatus.Done;
            }
        }
        catch (OperationCanceledException)
        {
            return; // stays approved; the next Start re-runs the cut
        }
        catch (Exception ex)
        {
            _log.Error($"{label} 승인 컷 처리 중 오류", ex);
            status = PendingSplitStatus.Failed;
            failReason = $"컷 처리 오류: {ex.Message}";
        }

        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Splits.FindIndex(s => s.Id == item.Id);
            if (index >= 0)
            {
                state.Splits[index] = state.Splits[index] with
                {
                    Status = status, Title = title, FailReason = failReason
                };
            }
            PruneTerminalLocked();
            PersistLocked();
        }
        RaiseChanged();
    }

    /// <summary>Waits until <paramref name="wakeAtLocal"/> (or forever) unless nudged. False = cancelled.</summary>
    private async Task<bool> WaitAsync(DateTime? wakeAtLocal, CancellationToken ct)
    {
        var signal = _signal;
        try
        {
            if (wakeAtLocal is { } at)
            {
                var span = at - _now();
                if (span <= TimeSpan.Zero)
                {
                    return true;
                }
                var completed = await Task.WhenAny(_delay(span, ct), signal.Task).ConfigureAwait(false);
                if (completed == signal.Task)
                {
                    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            else
            {
                await signal.Task.WaitAsync(ct).ConfigureAwait(false);
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---- helpers ----

    private PendingSplit NewPending(
        DateOnly date, IReadOnlyList<int> periods, DateTime start, DateTime end,
        string sessionFile, DateTime sessionStart)
    {
        var now = _now();
        // The deadline counts from now (not the boundary) so a late-created split still gives the
        // operator the full decision window. Config is read at creation; later edits affect new splits.
        var minutes = _configStore.Load().Periods.AutoApproveMinutes;
        var segments = _sessionInfo.GetSegments(start, end);
        if (segments.Count == 0)
        {
            segments = [new RecordingSegment(sessionFile, sessionStart, null)];
        }
        return new PendingSplit(
            _idFactory(), date, periods, start, end, sessionFile, sessionStart,
            PendingSplitStatus.Pending, now,
            minutes is { } m ? now.AddMinutes(m) : null,
            null, null, null, null, null, segments);
    }

    private SchoolPeriod? NextScheduledPeriod(DateOnly date, int afterNumber) =>
        _scheduleStore.ResolveForDate(date).Periods
            .Where(p => p.Number > afterNumber)
            .OrderBy(p => p.Number)
            .FirstOrDefault();

    private static DateTime? Earlier(DateTime? a, DateTime b) => a is { } x && x <= b ? x : b;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Caps retained terminal splits so the state file stays bounded. Caller holds _gate.</summary>
    private void PruneTerminalLocked()
    {
        var splits = LoadLocked().Splits; // insertion order: oldest first
        var terminal = splits.Where(s =>
            s.Status is PendingSplitStatus.Done or PendingSplitStatus.Skipped or PendingSplitStatus.Failed)
            .ToList();
        if (terminal.Count <= _maxTerminalRetained)
        {
            return;
        }
        var drop = new HashSet<string>(
            terminal.Take(terminal.Count - _maxTerminalRetained).Select(s => s.Id),
            StringComparer.Ordinal);
        splits.RemoveAll(s => drop.Contains(s.Id));
    }

    // ---- persistence (UploadQueue pattern) ----

    private SplitState LoadLocked()
    {
        if (_state is not null)
        {
            return _state;
        }
        if (!File.Exists(_stateFile))
        {
            return _state = new SplitState();
        }
        try
        {
            var json = File.ReadAllText(_stateFile);
            _state = JsonSerializer.Deserialize<SplitState>(json, JsonOptions) ?? new SplitState();
        }
        catch (JsonException)
        {
            File.Copy(_stateFile, _stateFile + ".bak", overwrite: true);
            _state = new SplitState();
        }
        return _state;
    }

    private void PersistLocked()
    {
        var dir = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _stateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(tmp, _stateFile, overwrite: true);
    }

    /// <summary>Boundary-shift memory: the last approved end and the scheduled end it replaced.</summary>
    private sealed record LastApproval(DateOnly Date, DateTime ScheduledEndLocal, DateTime ResolvedEndLocal);

    private sealed class SplitState
    {
        public List<PendingSplit> Splits { get; set; } = [];
        public MergeChain? Chain { get; set; }
        public LastApproval? LastApproved { get; set; }
    }
}
