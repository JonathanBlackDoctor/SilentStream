using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Wires the per-period VOD pipeline together (확장계획서 §4.0 data flow): on each
/// <see cref="IPeriodScheduler.PeriodEnded"/> it cuts the period from the session recording,
/// then enqueues an unlisted upload titled "{교시} - 날짜". Starts the wall-clock scheduler and
/// the persistent upload worker. Entirely read-only w.r.t. the live/tee pipeline (§2.3, R5).
/// </summary>
public sealed class VodCoordinator
{
    private readonly IPeriodScheduler _scheduler;
    private readonly IVodSegmentService _vod;
    private readonly IUploadQueue _queue;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly ILocalAudioExportService _audioExport;
    private readonly IPeriodAssetCatalog _assets;
    private readonly Func<string> _idFactory;
    private readonly IRecordingSessionInfo? _sessions;
    private readonly IPeriodScheduleStore? _schedules;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _workGate = new(1, 1);

    private int _started;

    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILogService log,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        IRecordingSessionInfo? sessions = null,
        IPeriodScheduleStore? schedules = null)
        : this(scheduler, vod, queue, configStore, log, audioExport, assets,
            () => Guid.NewGuid().ToString("N"), sessions, schedules)
    {
    }

    /// <summary>Test seam: deterministic job ids.</summary>
    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILogService log,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        Func<string> idFactory,
        IRecordingSessionInfo? sessions = null,
        IPeriodScheduleStore? schedules = null,
        Func<DateTime>? now = null)
    {
        _scheduler = scheduler;
        _vod = vod;
        _queue = queue;
        _configStore = configStore;
        _log = log;
        _audioExport = audioExport;
        _assets = assets;
        _idFactory = idFactory;
        _sessions = sessions;
        _schedules = schedules;
        _now = now ?? (() => DateTime.Now);
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return; // idempotent
        }

        _queue.Start(ct);
        _scheduler.PeriodEnded += (_, boundary) => _ = OnPeriodEndedAsync(boundary, ct);
        _scheduler.Start(ct);
        if (_sessions is not null && _schedules is not null) _ = RecoverLoopAsync(ct);
        _log.Info("고정 시간표 VOD 코디네이터를 시작했습니다(자동 분할 + 업로드 워커).");
    }

    private async Task OnPeriodEndedAsync(Models.PeriodBoundary boundary, CancellationToken ct)
    {
        var entered = false;
        try
        {
            await _workGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var known = _queue.Snapshot();
            // A legacy, unkeyed upload cannot be matched safely to one historical source part.
            if (known.Any(job => job.Date == boundary.Date && job.ResolvedSlotId == boundary.ResolvedSlotId
                && job.SourceKey is null)) return;
            var queuedParts = known.Where(job => job.SourceKey is not null)
                .Select(job => job.SourceKey!).ToHashSet(StringComparer.Ordinal);
            var clips = await _vod.ExtractPeriodPartsAsync(boundary, queuedParts, ct).ConfigureAwait(false);
            if (clips.Count == 0)
            {
                return;
            }

            var config = _configStore.Load();
            var room = TitleTemplater.ResolveRoomName(config);
            var roomPart = string.IsNullOrWhiteSpace(room) ? string.Empty : $" {room}";
            foreach (var clip in clips)
            {
            try
            {
            var path = clip.FilePath;
            var slotLabel = boundary.ResolvedSlotLabel + (clip.PartCount > 1 ? $" ({clip.PartNumber}/{clip.PartCount})" : "");
            var title = $"[영상]{roomPart} | {boundary.Date:yyyy-MM-dd} | {slotLabel}";
            var id = clip.SourceKey is { } key ? key[..32] : _idFactory();

            // Export from the app's local cut before the upload worker removes its temporary MP4.
            // This is intentionally independent of YouTube: no audiovisual content is downloaded
            // from, or separated through, a YouTube API.
            var audioPath = await _audioExport.ExportAsync(path, id, ct).ConfigureAwait(false);
            try
            {
                _assets.Upsert(new PeriodAsset(
                    id, boundary.Date, boundary.PeriodNumber, title, AudioPath: audioPath,
                    SlotId: boundary.ResolvedSlotId,
                    SlotLabel: slotLabel,
                    SlotKind: boundary.ResolvedSlotKind,
                    SlotOrder: boundary.ResolvedSlotOrder, SourceKey: clip.SourceKey));
            }
            catch (Exception ex)
            {
                // The durable-download catalogue is additive. Losing it must never sacrifice the
                // already-cut VOD or block its YouTube upload.
                _log.Error($"{boundary.ResolvedSlotLabel} 다운로드 자산 목록 저장 실패", ex);
            }

            _queue.Enqueue(new UploadJob(
                id, path, title, boundary.Date, boundary.PeriodNumber,
                UploadJobStatus.Pending, 0, null,
                boundary.ResolvedSlotId,
                slotLabel,
                boundary.ResolvedSlotKind,
                boundary.ResolvedSlotOrder, SourceKey: clip.SourceKey));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Error($"{boundary.ResolvedSlotLabel} 부분 영상 처리 실패", ex); }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"{boundary.ResolvedSlotLabel} VOD 처리 중 오류", ex);
        }
        finally { if (entered) _workGate.Release(); }
    }

    public async Task RecoverCompletedPeriodsAsync(CancellationToken ct)
    {
        if (_sessions is null || _schedules is null) return;
        var now = _now();
        var today = DateOnly.FromDateTime(now);
        for (var offset = -6; offset <= 0; offset++)
        {
            var date = today.AddDays(offset);
            foreach (var slot in _schedules.ResolveForDate(date).Periods)
            {
                var start = date.ToDateTime(slot.Start); var end = date.ToDateTime(slot.End);
                if (end > now || _sessions.GetSegments(start, end).Count == 0) continue;
                await OnPeriodEndedAsync(new PeriodBoundary(date, slot.Number, start, end,
                    slot.ResolvedSlotId, slot.ResolvedSlotLabel, slot.ResolvedSlotKind, slot.ResolvedSlotOrder), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RecoverLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RecoverCompletedPeriodsAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.Error("중단된 교시 영상 복구 점검 실패", ex);
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
