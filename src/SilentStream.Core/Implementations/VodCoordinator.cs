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
    private readonly ISplitApprovalService _splits;
    private readonly ILogService _log;
    private readonly ILocalAudioExportService _audioExport;
    private readonly IPeriodAssetCatalog _assets;
    private readonly Func<string> _idFactory;

    private int _started;

    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ISplitApprovalService splits,
        ILogService log,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets)
        : this(scheduler, vod, queue, configStore, splits, log, audioExport, assets,
            () => Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>Test seam: deterministic job ids.</summary>
    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ISplitApprovalService splits,
        ILogService log,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        Func<string> idFactory)
    {
        _scheduler = scheduler;
        _vod = vod;
        _queue = queue;
        _configStore = configStore;
        _splits = splits;
        _log = log;
        _audioExport = audioExport;
        _assets = assets;
        _idFactory = idFactory;
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return; // idempotent
        }

        _queue.Start(ct);
        _splits.Start(ct);
        _scheduler.PeriodEnded += (_, boundary) => _ = OnPeriodEndedAsync(boundary, ct);
        _scheduler.Start(ct);
        _log.Info("교시 VOD 코디네이터를 시작했습니다(스케줄러 + 승인 + 업로드 워커).");
    }

    private async Task OnPeriodEndedAsync(Models.PeriodBoundary boundary, CancellationToken ct)
    {
        try
        {
            if (_configStore.Load().Periods.RequireApproval)
            {
                // Approval mode (v8 default): record a pending split for the phone instead of
                // cutting now — the approval service runs the cut → audio export → upload chain
                // once the operator (or the auto-approve deadline) confirms the window. The
                // immediate path below remains for RequireApproval=false.
                _splits.OnPeriodEnded(boundary);
                return;
            }

            var path = await _vod.ExtractPeriodAsync(boundary, ct).ConfigureAwait(false);
            if (path is null)
            {
                _log.Warn($"{boundary.PeriodNumber}교시 VOD 컷이 비어 업로드를 건너뜁니다.");
                return;
            }

            var config = _configStore.Load();
            var periods = config.Periods;
            var title = TitleTemplater.Expand(
                periods.TitleTemplate, boundary.StartLocal, boundary.PeriodNumber,
                TitleTemplater.ResolveRoomName(config));
            var id = _idFactory();

            // Export from the app's local cut before the upload worker removes its temporary MP4.
            // This is intentionally independent of YouTube: no audiovisual content is downloaded
            // from, or separated through, a YouTube API.
            var audioPath = await _audioExport.ExportAsync(path, id, ct).ConfigureAwait(false);
            try
            {
                _assets.Upsert(new PeriodAsset(
                    id, boundary.Date, boundary.PeriodNumber, title, AudioPath: audioPath));
            }
            catch (Exception ex)
            {
                // The durable-download catalogue is additive. Losing it must never sacrifice the
                // already-cut VOD or block its YouTube upload.
                _log.Error($"{boundary.PeriodNumber}교시 다운로드 자산 목록 저장 실패", ex);
            }

            _queue.Enqueue(new UploadJob(
                id, path, title, boundary.Date, boundary.PeriodNumber,
                UploadJobStatus.Pending, 0, null));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"{boundary.PeriodNumber}교시 VOD 처리 중 오류", ex);
        }
    }
}
