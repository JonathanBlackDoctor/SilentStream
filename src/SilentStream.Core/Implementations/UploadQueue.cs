using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Persistent, quota-aware upload queue (확장계획서 §4.2). Jobs are stored as JSON so they survive
/// reboots; a single background worker uploads them oldest-first, never exceeds the daily cap
/// (~6 inserts/day at 1,600 units each against the default 10,000), and pauses on a 403 quota
/// error until the Pacific-time reset before resuming — so 7교시+ overflow uploads the next day (D7).
/// </summary>
public sealed class UploadQueue : IUploadQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Lazy<TimeZoneInfo> PacificZone = new(ResolvePacificZone);

    private readonly IYouTubeUploadService _uploadService;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly IPeriodAssetCatalog? _assets;
    private readonly string _queueFile;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxPerDay;
    private readonly int _maxAttempts;
    // Keep recent terminal jobs (phone visibility / manual retry) but prune older ones so
    // upload_queue.json cannot grow without bound. Pending/Uploading jobs are never pruned.
    private readonly int _maxCompletedRetained;
    private readonly int _maxFailedRetained;

    private readonly object _gate = new();
    private QueueState? _state;
    private Task? _worker;
    private volatile TaskCompletionSource _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UploadQueue(IYouTubeUploadService uploadService, IConfigStore configStore, ILogService log)
        : this(uploadService, configStore, log, AppPaths.UploadQueueFile, () => DateTime.Now, Task.Delay,
            maxPerDay: 6, maxAttempts: 5)
    {
    }

    /// <summary>
    /// DI constructor: links a successful upload to its durable download asset without changing
    /// the queue's retry semantics if the optional catalogue cannot be written.
    /// </summary>
    public UploadQueue(
        IYouTubeUploadService uploadService,
        IConfigStore configStore,
        ILogService log,
        IPeriodAssetCatalog assets)
        : this(uploadService, configStore, log, AppPaths.UploadQueueFile, () => DateTime.Now, Task.Delay,
            maxPerDay: 6, maxAttempts: 5, assetCatalog: assets)
    {
    }

    /// <summary>Test seam: redirect the queue file, clock, delay, and limits.</summary>
    public UploadQueue(
        IYouTubeUploadService uploadService,
        IConfigStore configStore,
        ILogService log,
        string queueFile,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay,
        int maxPerDay,
        int maxAttempts,
        int maxCompletedRetained = 50,
        int maxFailedRetained = 20,
        IPeriodAssetCatalog? assetCatalog = null)
    {
        _uploadService = uploadService;
        _configStore = configStore;
        _log = log;
        _queueFile = queueFile;
        _now = now;
        _delay = delay;
        _maxPerDay = maxPerDay;
        _maxAttempts = maxAttempts;
        _maxCompletedRetained = maxCompletedRetained;
        _maxFailedRetained = maxFailedRetained;
        _assets = assetCatalog;
    }

    public void Enqueue(UploadJob job)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            state.Jobs.Add(job);
            PersistLocked();
        }
        _log.Info($"업로드 큐에 적재: {job.PeriodNumber}교시 \"{job.Title}\".");
        _signal.TrySetResult();
    }

    public IReadOnlyList<UploadJob> Snapshot()
    {
        lock (_gate)
        {
            return LoadLocked().Jobs.ToList();
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
            // Crash recovery: a job left mid-upload by a previous run returns to the queue.
            var state = LoadLocked();
            var requeued = 0;
            for (var i = 0; i < state.Jobs.Count; i++)
            {
                if (state.Jobs[i].Status == UploadJobStatus.Uploading)
                {
                    state.Jobs[i] = state.Jobs[i] with { Status = UploadJobStatus.Pending };
                    requeued++;
                }
            }
            if (requeued > 0)
            {
                PersistLocked();
            }
            PruneTerminalLocked();
            _worker = WorkerAsync(ct);
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        _log.Info("VOD 업로드 워커를 시작했습니다.");
        while (!ct.IsCancellationRequested)
        {
            var job = NextPending();
            if (job is null)
            {
                if (!await WaitForSignalAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }

            ResetQuotaWindowIfNewDay();
            if (QuotaSpent())
            {
                var resumeAt = NextResetLocal(_now());
                _log.Info($"일일 업로드 한도({_maxPerDay}건) 도달 — {resumeAt:yyyy-MM-dd HH:mm}(현지) 리셋까지 대기.");
                if (!await DelayUntilAsync(resumeAt, ct).ConfigureAwait(false))
                {
                    return;
                }
                continue; // recompute window after reset
            }

            await TryUploadAsync(job, ct).ConfigureAwait(false);
        }
    }

    private async Task TryUploadAsync(UploadJob job, CancellationToken ct)
    {
        UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Uploading });
        try
        {
            var privacy = _configStore.Load().Periods.VodPrivacy;
            var videoId = await _uploadService
                .UploadAsync(job.FilePath, job.Title, privacy, ct).ConfigureAwait(false);

            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Completed, VideoId = videoId });
            try
            {
                _assets?.MarkUploaded(job.Id, videoId);
            }
            catch (Exception ex)
            {
                // The upload already succeeded. Preserve that terminal state even if the optional
                // download-asset metadata cannot be refreshed right now.
                _log.Warn($"{job.PeriodNumber}교시 다운로드 자산의 YouTube 연결 저장 실패: {ex.Message}");
            }
            IncrementQuota();
            TryDeleteCutFile(job.FilePath);
            PruneTerminal();
        }
        catch (QuotaExceededException)
        {
            // Keep the job pending and force a pause until the next reset (D7).
            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Pending });
            SetQuotaSpent();
            _log.Warn($"{job.PeriodNumber}교시 업로드 quota 초과 — 큐에 보존하고 리셋 후 자동 재개합니다.");
        }
        catch (OperationCanceledException)
        {
            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Pending });
        }
        catch (Exception ex)
        {
            var attempts = job.Attempts + 1;
            if (attempts >= _maxAttempts)
            {
                UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Failed, Attempts = attempts });
                _log.Error($"{job.PeriodNumber}교시 업로드 {attempts}회 실패 — failed 처리(수동 재시도 필요).", ex);
                PruneTerminal();
            }
            else
            {
                UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Pending, Attempts = attempts });
                _log.Warn($"{job.PeriodNumber}교시 업로드 실패({attempts}/{_maxAttempts}) — 재시도 예정: {ex.Message}");
                await BackoffAsync(attempts, ct).ConfigureAwait(false);
            }
        }
    }

    private UploadJob? NextPending()
    {
        lock (_gate)
        {
            return LoadLocked().Jobs.FirstOrDefault(j => j.Status == UploadJobStatus.Pending);
        }
    }

    private void UpdateJob(string id, Func<UploadJob, UploadJob> transform)
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var index = state.Jobs.FindIndex(j => j.Id == id);
            if (index >= 0)
            {
                state.Jobs[index] = transform(state.Jobs[index]);
                PersistLocked();
            }
        }
    }

    private void PruneTerminal()
    {
        lock (_gate)
        {
            PruneTerminalLocked();
        }
    }

    /// <summary>
    /// Caps retained terminal jobs (most-recent <see cref="MaxCompletedRetained"/> completed and
    /// <see cref="MaxFailedRetained"/> failed) so the queue file stays bounded. Pending/Uploading
    /// jobs are never removed — dropping one would lose an unsent 교시. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void PruneTerminalLocked()
    {
        var jobs = LoadLocked().Jobs; // insertion order: oldest first
        var completed = jobs.Where(j => j.Status == UploadJobStatus.Completed).ToList();
        var failed = jobs.Where(j => j.Status == UploadJobStatus.Failed).ToList();
        if (completed.Count <= _maxCompletedRetained && failed.Count <= _maxFailedRetained)
        {
            return;
        }

        var drop = new HashSet<string>(StringComparer.Ordinal);
        if (completed.Count > _maxCompletedRetained)
        {
            foreach (var j in completed.Take(completed.Count - _maxCompletedRetained))
            {
                drop.Add(j.Id);
            }
        }
        if (failed.Count > _maxFailedRetained)
        {
            foreach (var j in failed.Take(failed.Count - _maxFailedRetained))
            {
                drop.Add(j.Id);
            }
        }

        if (jobs.RemoveAll(j => drop.Contains(j.Id)) > 0)
        {
            PersistLocked();
        }
    }

    // ---- quota accounting ----

    private void ResetQuotaWindowIfNewDay()
    {
        lock (_gate)
        {
            var state = LoadLocked();
            var ptToday = PacificDateString(_now());
            if (state.QuotaPtDate != ptToday)
            {
                state.QuotaPtDate = ptToday;
                state.QuotaCount = 0;
                PersistLocked();
            }
        }
    }

    private bool QuotaSpent()
    {
        lock (_gate) { return LoadLocked().QuotaCount >= _maxPerDay; }
    }

    private void IncrementQuota()
    {
        lock (_gate)
        {
            LoadLocked().QuotaCount++;
            PersistLocked();
        }
    }

    private void SetQuotaSpent()
    {
        lock (_gate)
        {
            LoadLocked().QuotaCount = _maxPerDay;
            PersistLocked();
        }
    }

    // ---- waiting ----

    private async Task<bool> WaitForSignalAsync(CancellationToken ct)
    {
        var signal = _signal;
        try
        {
            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private async Task<bool> DelayUntilAsync(DateTime resumeAtLocal, CancellationToken ct)
    {
        var span = resumeAtLocal - _now();
        if (span <= TimeSpan.Zero)
        {
            return true;
        }
        try
        {
            await _delay(span, ct).ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task BackoffAsync(int attempts, CancellationToken ct)
    {
        var seconds = Math.Min(60, 5 * Math.Pow(2, attempts - 1));
        try
        {
            await _delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- persistence ----

    private QueueState LoadLocked()
    {
        if (_state is not null)
        {
            return _state;
        }
        if (!File.Exists(_queueFile))
        {
            return _state = new QueueState();
        }
        try
        {
            var json = File.ReadAllText(_queueFile);
            _state = JsonSerializer.Deserialize<QueueState>(json, JsonOptions) ?? new QueueState();
        }
        catch (JsonException)
        {
            File.Copy(_queueFile, _queueFile + ".bak", overwrite: true);
            _state = new QueueState();
        }
        return _state;
    }

    private void PersistLocked()
    {
        var dir = Path.GetDirectoryName(_queueFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _queueFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(tmp, _queueFile, overwrite: true);
    }

    private void TryDeleteCutFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _log.Info($"업로드 완료 후 컷 파일 삭제: {Path.GetFileName(path)}");
            }
        }
        catch (IOException ex)
        {
            _log.Debug($"업로드 완료 후 컷 파일 삭제 실패: {ex.Message}");
        }
    }

    // ---- Pacific-time helpers (YouTube quota resets at PT midnight) ----

    private static string PacificDateString(DateTime localNow) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(localNow), PacificZone.Value)
            .ToString("yyyy-MM-dd");

    private static DateTime NextResetLocal(DateTime localNow)
    {
        var zone = PacificZone.Value;
        var ptNow = TimeZoneInfo.ConvertTime(new DateTimeOffset(localNow), zone);
        var nextMidnight = ptNow.Date.AddDays(1).AddSeconds(1); // 1s cushion past midnight
        var offset = zone.GetUtcOffset(nextMidnight);
        return new DateTimeOffset(nextMidnight, offset).ToLocalTime().DateTime;
    }

    private static TimeZoneInfo ResolvePacificZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Utc;
    }

    private sealed class QueueState
    {
        public List<UploadJob> Jobs { get; set; } = [];

        /// <summary>Pacific-time date ("yyyy-MM-dd") the quota counter currently belongs to.</summary>
        public string QuotaPtDate { get; set; } = string.Empty;

        /// <summary>Number of successful inserts counted against today's quota.</summary>
        public int QuotaCount { get; set; }
    }
}
