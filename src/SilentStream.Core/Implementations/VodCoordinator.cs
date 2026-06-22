using SilentStream.Core.Contracts;
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
    private readonly Func<string> _idFactory;

    private int _started;

    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILogService log)
        : this(scheduler, vod, queue, configStore, log, () => Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>Test seam: deterministic job ids.</summary>
    public VodCoordinator(
        IPeriodScheduler scheduler,
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILogService log,
        Func<string> idFactory)
    {
        _scheduler = scheduler;
        _vod = vod;
        _queue = queue;
        _configStore = configStore;
        _log = log;
        _idFactory = idFactory;
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
        _log.Info("교시 VOD 코디네이터를 시작했습니다(스케줄러 + 업로드 워커).");
    }

    private async Task OnPeriodEndedAsync(Models.PeriodBoundary boundary, CancellationToken ct)
    {
        try
        {
            var path = await _vod.ExtractPeriodAsync(boundary, ct).ConfigureAwait(false);
            if (path is null)
            {
                _log.Warn($"{boundary.PeriodNumber}교시 VOD 컷이 비어 업로드를 건너뜁니다.");
                return;
            }

            var config = _configStore.Load();
            var periods = config.Periods;
            var title = TitleTemplater.Expand(
                periods.TitleTemplate, boundary.StartLocal, boundary.PeriodNumber, config.DeviceName);
            _queue.Enqueue(new UploadJob(
                _idFactory(), path, title, boundary.Date, boundary.PeriodNumber,
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
