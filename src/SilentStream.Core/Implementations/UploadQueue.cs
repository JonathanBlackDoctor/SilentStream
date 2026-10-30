using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Persistent, quota-aware upload queue (확장계획서 §4.2). Jobs are stored as JSON so they survive
/// reboots; a single background worker uploads them oldest-first until YouTube reports actual
/// quota exhaustion, and pauses on that quota response
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
    private readonly IYouTubePlaylistService? _playlists;
    private readonly string _queueFile;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxPerDay;
    private readonly int _maxAttempts;
    // Keep recent terminal jobs (phone visibility / manual retry) but prune older ones so
    // upload_queue.json cannot grow without bound. Pending/Uploading jobs are never pruned.
    private readonly int _maxCompletedRetained;

    private readonly object _gate = new();
    private QueueState? _state;
    private Task? _worker;
    private bool _persistenceDirty;
    private volatile TaskCompletionSource _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UploadQueue(IYouTubeUploadService uploadService, IConfigStore configStore, ILogService log)
        : this(uploadService, configStore, log, AppPaths.UploadQueueFile, () => DateTime.Now, Task.Delay,
            maxPerDay: int.MaxValue, maxAttempts: 5)
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
            maxPerDay: int.MaxValue, maxAttempts: 5, assetCatalog: assets)
    {
    }

    public UploadQueue(
        IYouTubeUploadService uploadService,
        IConfigStore configStore,
        ILogService log,
        IPeriodAssetCatalog assets,
        IYouTubePlaylistService playlists)
        : this(uploadService, configStore, log, AppPaths.UploadQueueFile, () => DateTime.Now, Task.Delay,
            maxPerDay: int.MaxValue, maxAttempts: 5, assetCatalog: assets, playlistService: playlists)
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
        IPeriodAssetCatalog? assetCatalog = null,
        IYouTubePlaylistService? playlistService = null)
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
        _assets = assetCatalog;
        _playlists = playlistService;
    }

    public void Enqueue(UploadJob job)
    {
        if (job.FileSizeBytes is null)
        {
            try
            {
                job = job with { FileSizeBytes = new FileInfo(job.FilePath).Length };
            }
            catch
            {
                // The cutter and worker will surface a missing/unreadable file in the normal path.
            }
        }
        lock (_gate)
        {
            var state = LoadLocked();
            if (state.Jobs.Any(existing => existing.Id == job.Id ||
                (!string.IsNullOrWhiteSpace(job.SourceKey) && existing.SourceKey == job.SourceKey))) return;
            if (_assets?.Snapshot().Any(a => !string.IsNullOrWhiteSpace(a.VideoId) &&
                (a.Id == job.Id || (!string.IsNullOrWhiteSpace(job.SourceKey) && a.SourceKey == job.SourceKey))) == true) return;
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

    public DateTimeOffset? PausedUntilUtc
    {
        get
        {
            lock (_gate)
            {
                var state = LoadLocked();
                var now = _now();
                DateTimeOffset? apiPause = state.QuotaPtDate == PacificDateString(now) && state.QuotaCount >= _maxPerDay
                    ? new DateTimeOffset(NextResetLocal(now)).ToUniversalTime()
                    : null;
                var channelPause = state.UploadLimitUntilUtc > UtcNow() ? state.UploadLimitUntilUtc : null;
                return apiPause is { } api && (channelPause is null || api > channelPause) ? api : channelPause;
            }
        }
    }

    public int RetryFailed(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count is 0 or > 50) throw new ArgumentException("재시도는 1~50개 항목을 선택하세요.", nameof(ids));
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var count = 0;
        lock (_gate)
        {
            var state = LoadLocked();
            for (var i = 0; i < state.Jobs.Count; i++)
            {
                var job = state.Jobs[i];
                if (!selected.Contains(job.Id) || job.Status != UploadJobStatus.Failed || job.NeedsUploadReconciliation ||
                    !string.IsNullOrWhiteSpace(job.VideoId) || !Media.Mp4MediaSamples.HasUploadableVideo(job.FilePath))
                    continue;
                state.Jobs[i] = job with
                {
                    Status = UploadJobStatus.Pending,
                    Attempts = 0,
                    UploadStartedAtUtc = null,
                    UploadCompletedAtUtc = null,
                    UploadDurationSeconds = null
                };
                count++;
            }
            if (count > 0) PersistLocked();
        }
        if (count > 0)
        {
            _log.Info($"선택한 실패 업로드 {count}건을 재시도 큐에 보존했습니다.");
            _signal.TrySetResult();
        }
        return count;
    }

    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false })
            {
                return; // idempotent
            }
            var initialized = false;
            try { InitializeQueueLocked(); initialized = true; }
            catch (Exception ex) { _log.Error("업로드 기록을 읽지 못했습니다. 녹화는 계속하고 업로드 기록 복구를 기다립니다.", ex); }
            _worker = WorkerAsync(ct, initialized);
        }
    }

    private void InitializeQueueLocked()
    {
        // Reconcile durable receipts first. A lost acknowledgement cannot safely be retried:
        // YouTube may already have accepted the video before the local process stopped.
        var state = LoadLocked();
        var receipts = _assets?.Snapshot() ?? [];
        RecoverLegacyQuotaFailuresLocked(state);
        var requeued = 0;
        for (var i = 0; i < state.Jobs.Count; i++)
        {
            var job = state.Jobs[i];
            var videoId = job.VideoId ?? receipts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.VideoId) &&
                (a.Id == job.Id || (!string.IsNullOrWhiteSpace(job.SourceKey) && a.SourceKey == job.SourceKey)))?.VideoId;
            if (!string.IsNullOrWhiteSpace(videoId) && job.Status is UploadJobStatus.Pending or UploadJobStatus.Uploading)
            {
                state.Jobs[i] = job with
                {
                    Status = UploadJobStatus.Completed,
                    VideoId = videoId,
                    NeedsUploadReconciliation = false
                };
                requeued++;
            }
            else if (job.Status == UploadJobStatus.Uploading)
            {
                state.Jobs[i] = job with { Status = UploadJobStatus.Failed, NeedsUploadReconciliation = true };
                _log.Warn($"{job.ResolvedSlotLabel} 중단된 업로드의 완료 여부 확인 필요 — 중복 전송을 막기 위해 자동 재시도를 보류합니다.");
                requeued++;
            }
        }
        if (requeued > 0)
        {
            PersistLocked();
        }
        PruneTerminalLocked();
    }

    // Releases before 0.4.22 exhausted the retry budget on YouTube quota responses. Repair
    // one prior failed class day when a persisted channel-limit pause proves this queue saw
    // the outage. Version 1 selected the newest date across ALL jobs, so newer pending work
    // could hide the failures. Version 2 must also handle those already-marked queues without
    // resetting jobs that version 1 retried on a later day and that failed for another reason.
    private void RecoverLegacyQuotaFailuresLocked(QueueState state)
    {
        if (state.QuotaRecoveryVersion >= 2) return;
        var now = _now();
        var today = DateOnly.FromDateTime(now);
        DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(
            now.Kind == DateTimeKind.Utc ? value.UtcDateTime : value.LocalDateTime);
        // Only 0.4.22+ writes this 24-hour pause. Failures from its observation day or later
        // are not evidence of the old quota-classification bug. Never clear even an expired pause.
        var quotaObserved = state.UploadLimitUntilUtc?.AddHours(-24);
        var quotaDate = quotaObserved is { } observed ? LocalDate(observed) : today;
        var date = state.Jobs.Where(j => j.Status == UploadJobStatus.Failed &&
                j.Date < today && j.Date < quotaDate && j.Date >= today.AddDays(-7))
            .Select(j => (DateOnly?)j.Date).Max();
        var recovered = 0;
        if (quotaObserved is not null && date is not null)
        {
            // Missing/unreadable receipts defer the migration instead of consuming it.
            if (_assets is null) return;
            IReadOnlyList<Models.PeriodAsset> assets;
            try { assets = _assets.Snapshot(); }
            catch (Exception ex)
            {
                _log.Warn($"기존 한도 실패 복구를 보류합니다. 업로드 완료 기록 확인 실패: {ex.Message}");
                return;
            }
            var completedIds = assets.Where(a => !string.IsNullOrWhiteSpace(a.VideoId))
                .Select(a => a.Id).Concat(state.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.VideoId)).Select(j => j.Id))
                .ToHashSet(StringComparer.Ordinal);
            var completedSources = assets.Where(a => !string.IsNullOrWhiteSpace(a.VideoId)).Select(a => a.SourceKey)
                .Concat(state.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.VideoId)).Select(j => j.SourceKey))
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < state.Jobs.Count; i++)
            {
                var job = state.Jobs[i];
                if (job.Date != date || job.Status != UploadJobStatus.Failed || job.Attempts < _maxAttempts ||
                    completedIds.Contains(job.Id) ||
                    (!string.IsNullOrWhiteSpace(job.SourceKey) && completedSources.Contains(job.SourceKey)) ||
                    !Media.Mp4MediaSamples.HasUploadableVideo(job.FilePath)) continue;
                // Version 1 only repaired past dates, so an attempt after the class day may
                // already be its retry. Unknown timestamps on migrated queues are ambiguous:
                // leave them for explicit review instead of automatically granting more attempts.
                if (job.UploadStartedAtUtc is { } attempted)
                {
                    if (attempted >= quotaObserved || (state.QuotaRecoveryVersion == 1 && LocalDate(attempted) > job.Date))
                        continue;
                }
                else if (state.QuotaRecoveryVersion == 1) continue;
                state.Jobs[i] = job with
                {
                    Status = UploadJobStatus.Pending,
                    Attempts = 0,
                    UploadStartedAtUtc = null,
                    UploadCompletedAtUtc = null,
                    UploadDurationSeconds = null
                };
                recovered++;
            }
        }
        state.QuotaRecoveryVersion = 2;
        PersistLocked();
        if (recovered > 0) _log.Info($"기존 한도 실패 {recovered}건을 {date:yyyy-MM-dd} 재시도 큐로 1회 복구했습니다. 한도 대기는 유지합니다.");
    }

    private async Task WorkerAsync(CancellationToken ct, bool initialized)
    {
        _log.Info("VOD 업로드 워커를 시작했습니다.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    lock (_gate) InitializeQueueLocked();
                    initialized = true;
                }
                // An acknowledged success remains terminal in memory if disk writes fail. Repair
                // persistence before any further network operation; never turn it into a new upload.
                lock (_gate) { if (_persistenceDirty) PersistLocked(); }
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
                if (PausedUntilUtc is { } resumeAt)
                {
                    _log.Info($"YouTube 업로드 한도 대기 — {resumeAt.ToLocalTime():yyyy-MM-dd HH:mm} 이후 재시도.");
                    if (!await DelayUntilAsync(resumeAt, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue; // recompute window after reset
                }

                await TryUploadAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.Error("업로드 상태 저장/처리 실패 — 기존 상태와 파일을 보존하고 저장 복구 후 계속합니다.", ex);
                try { await _delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task TryUploadAsync(UploadJob job, CancellationToken ct)
    {
        var startedAtUtc = UtcNow();
        UpdateJob(job.Id, j => j with
        {
            Status = UploadJobStatus.Uploading,
            UploadStartedAtUtc = startedAtUtc,
            UploadCompletedAtUtc = null,
            UploadDurationSeconds = null,
            FileSizeBytes = j.FileSizeBytes ?? TryGetFileSize(j.FilePath)
        });
        string videoId;
        try
        {
            var privacy = _configStore.Load().Periods.VodPrivacy;
            videoId = await _uploadService
                .UploadAsync(job.FilePath, job.Title, privacy, ct).ConfigureAwait(false);
        }
        catch (QuotaExceededException ex)
        {
            // Keep the job pending and force a pause until the next reset (D7).
            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Pending });
            if (ex.RetryAfter is { } delay)
            {
                lock (_gate)
                {
                    LoadLocked().UploadLimitUntilUtc = UtcNow().Add(delay);
                    PersistLocked();
                }
            }
            else SetQuotaSpent();
            _log.Warn($"{job.PeriodNumber}교시 업로드 quota 초과 — 큐에 보존하고 리셋 후 자동 재개합니다.");
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or UploadOutcomeUnknownException)
        {
            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Failed, NeedsUploadReconciliation = true });
            _log.Warn($"{job.ResolvedSlotLabel} 업로드 완료 여부 미확정 — 실제 상태를 확인하기 전 재전송하지 않습니다: {ex.Message}");
            return;
        }
        catch (InvalidDataException ex)
        {
            // The same invalid local media cannot improve through network retries. Preserve
            // it for inspection, fail once and let other valid recordings continue.
            UpdateJob(job.Id, j => j with { Status = UploadJobStatus.Failed, Attempts = job.Attempts + 1 });
            _log.Error($"{job.ResolvedSlotLabel} 영상 검증 실패 — 원본 확인 필요(자동 재시도 중지).", ex);
            return;
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
            return;
        }

        // Nothing below this point may requeue the video: the remote service acknowledged it.
        var completedAtUtc = UtcNow();
        var durationSeconds = Math.Max(0, (completedAtUtc - startedAtUtc).TotalSeconds);
        try
        {
            if (_assets is not null)
            {
                if (_assets.Find(job.Id) is null)
                    _assets.Upsert(new Models.PeriodAsset(job.Id, job.Date, job.PeriodNumber, job.Title,
                        SlotId: job.ResolvedSlotId, SlotLabel: job.ResolvedSlotLabel,
                        SlotKind: job.ResolvedSlotKind, SlotOrder: job.ResolvedSlotOrder, SourceKey: job.SourceKey));
                _assets.MarkUploaded(job.Id, videoId, job.FileSizeBytes ?? TryGetFileSize(job.FilePath),
                    Media.Mp4MediaSamples.HasUploadableVideo(job.FilePath) ? 1 : 0);
            }
        }
        catch (Exception ex) { _log.Warn($"{job.ResolvedSlotLabel} 업로드 영수증 저장 지연: {ex.Message}"); }
        UpdateJob(job.Id, j => j with
        {
            Status = UploadJobStatus.Completed,
            VideoId = videoId,
            UploadCompletedAtUtc = completedAtUtc,
            UploadDurationSeconds = durationSeconds
        });
        IncrementQuota();
        if (_playlists is not null)
        {
            try
            {
                var room = TitleTemplater.ResolveRoomName(_configStore.Load());
                await _playlists.AddVideoAsync(room, job.Date, videoId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.Warn($"{job.ResolvedSlotLabel} 재생목록 편입 지연: {ex.Message}"); }
        }
        _log.Info($"{job.ResolvedSlotLabel} VOD 업로드 완료 — {FormatDuration(durationSeconds)}, {FormatBytes(job.FileSizeBytes ?? TryGetFileSize(job.FilePath))}.");
        TryDeleteCutFile(job.FilePath);
        PruneTerminal();
    }

    private UploadJob? NextPending()
    {
        lock (_gate)
        {
            return LoadLocked().Jobs.FirstOrDefault(j => j.Status == UploadJobStatus.Pending);
        }
    }

    private DateTimeOffset UtcNow()
    {
        var value = _now();
        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        }
        return new DateTimeOffset(value).ToUniversalTime();
    }

    private static long? TryGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static string FormatDuration(double seconds) =>
        TimeSpan.FromSeconds(seconds) is var duration && duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}시간 {duration.Minutes}분 {duration.Seconds}초"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}분 {duration.Seconds}초"
                : $"{duration.Seconds}초";

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null => "파일 크기 확인 불가",
        >= 1_073_741_824 => $"{bytes.Value / 1_073_741_824d:F2} GB",
        >= 1_048_576 => $"{bytes.Value / 1_048_576d:F1} MB",
        _ => $"{bytes.Value / 1024d:F1} KB"
    };

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
    /// Caps completed history. Unresolved jobs retain their identities until explicitly reconciled;
    /// dropping one would lose evidence or cause another upload. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void PruneTerminalLocked()
    {
        var jobs = LoadLocked().Jobs; // insertion order: oldest first
        var completed = jobs.Where(j => j.Status == UploadJobStatus.Completed).ToList();
        if (completed.Count <= _maxCompletedRetained)
        {
            return;
        }

        var drop = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<Models.PeriodAsset> receipts;
        try { receipts = _assets?.Snapshot() ?? []; }
        catch (Exception ex) { _log.Warn($"업로드 영수증 확인 실패 — 완료 큐 기록도 보존: {ex.Message}"); return; }
        if (completed.Count > _maxCompletedRetained)
        {
            foreach (var j in completed.Take(completed.Count - _maxCompletedRetained))
            {
                if (receipts.Any(a => a.Id == j.Id && !string.IsNullOrWhiteSpace(a.VideoId) && a.VideoId == j.VideoId &&
                    (string.IsNullOrWhiteSpace(j.SourceKey) || a.SourceKey == j.SourceKey))) drop.Add(j.Id);
            }
        }
        // Failed jobs are unresolved evidence, not disposable history. Dropping their SourceKey
        // also lets VOD recovery cut and enqueue the same failure forever.

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

    private async Task<bool> DelayUntilAsync(DateTimeOffset resumeAtUtc, CancellationToken ct)
    {
        var span = resumeAtUtc - UtcNow();
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
            _state = JsonSerializer.Deserialize<QueueState>(json, JsonOptions)
                ?? throw new JsonException("Upload queue is null.");
            if (_state.Jobs is null) { _state = null; throw new JsonException("Upload jobs are null."); }
        }
        catch (JsonException ex)
        {
            // A blank replacement would discard quota state and receipts and upload duplicates.
            _state = null;
            throw new InvalidDataException("업로드 목록이 손상되었습니다. 원본을 보존하고 복구가 필요합니다.", ex);
        }
        return _state;
    }

    private void PersistLocked()
    {
        _persistenceDirty = true;
        var dir = Path.GetDirectoryName(_queueFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _queueFile + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, _queueFile, overwrite: true);
        _persistenceDirty = false;
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
        public int QuotaRecoveryVersion { get; set; }
        public DateTimeOffset? UploadLimitUntilUtc { get; set; }
        public List<UploadJob> Jobs { get; set; } = [];

        /// <summary>Pacific-time date ("yyyy-MM-dd") the quota counter currently belongs to.</summary>
        public string QuotaPtDate { get; set; } = string.Empty;

        /// <summary>Number of successful inserts counted against today's quota.</summary>
        public int QuotaCount { get; set; }
    }
}
