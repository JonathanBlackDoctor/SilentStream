using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Implementations;

/// <summary>
/// One-way compatibility worker for approval-era pending_splits.json files. Every unfinished item
/// is expanded back into its original immutable fixed slots; adjusted/merged user windows are not
/// used. Stable generated job ids make a restart after any successful enqueue idempotent.
/// </summary>
public sealed class LegacyPendingSplitMigrator
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
    private readonly ILocalAudioExportService _audioExport;
    private readonly IPeriodAssetCatalog _assets;
    private readonly ILogService _log;
    private readonly string _stateFile;

    public LegacyPendingSplitMigrator(
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log)
        : this(vod, queue, configStore, audioExport, assets, log, AppPaths.PendingSplitsFile) { }

    public LegacyPendingSplitMigrator(
        IVodSegmentService vod,
        IUploadQueue queue,
        IConfigStore configStore,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log,
        string stateFile)
    {
        _vod = vod;
        _queue = queue;
        _configStore = configStore;
        _audioExport = audioExport;
        _assets = assets;
        _log = log;
        _stateFile = Path.GetFullPath(stateFile);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(_stateFile)) return;

        LegacySplitState state;
        try
        {
            state = JsonSerializer.Deserialize<LegacySplitState>(
                await File.ReadAllTextAsync(_stateFile, ct).ConfigureAwait(false), JsonOptions)
                ?? new LegacySplitState();
            state.Splits ??= [];
        }
        catch (JsonException ex)
        {
            _log.Warn($"기존 분할 승인 데이터 마이그레이션을 건너뜁니다: {ex.Message}");
            return;
        }

        if (state.Chain is { } chain)
        {
            state.Splits.Add(new PendingSplit(
                StableId("chain", chain.Date, chain.Periods), chain.Date, chain.Periods,
                chain.StartLocal, chain.FallbackEndLocal, chain.SessionFilePath,
                chain.SessionStartLocal, PendingSplitStatus.Pending, DateTime.Now,
                null, null, null, null, null, null,
                [new RecordingSegment(chain.SessionFilePath, chain.SessionStartLocal, null)]));
            state.Chain = null;
            Persist(state);
        }

        for (var index = 0; index < state.Splits.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = state.Splits[index];
            if (item.Status is not (PendingSplitStatus.Pending or PendingSplitStatus.Approved)) continue;

            var slots = item.Periods
                .Distinct()
                .Select(number => FixedSchoolSchedule.FindPeriod(item.Date, number))
                .Where(slot => slot is not null)
                .Cast<SchoolPeriod>()
                .OrderBy(slot => slot.ResolvedSlotOrder)
                .ToArray();
            if (slots.Length == 0)
            {
                state.Splits[index] = item with
                {
                    Status = PendingSplitStatus.Skipped,
                    FailReason = "고정 시간표에 해당 슬롯이 없어 건너뜀"
                };
                Persist(state);
                continue;
            }

            var allSucceeded = true;
            foreach (var slot in slots)
            {
                var jobId = StableId(item.Id, item.Date, [slot.Number]);
                if (_queue.Snapshot().Any(job => string.Equals(job.Id, jobId, StringComparison.Ordinal))
                    || _assets.Find(jobId)?.VideoId is not null)
                {
                    continue;
                }

                var start = item.Date.ToDateTime(slot.Start);
                var end = item.Date.ToDateTime(slot.End);
                var segments = item.RecordingSegments?.Count > 0
                    ? item.RecordingSegments
                    : [new RecordingSegment(item.SessionFilePath, item.SessionStartLocal, null)];
                var path = await _vod.ExtractRangeAsync(
                    segments, start, end, slot.ResolvedSlotLabel, ct).ConfigureAwait(false);
                if (path is null)
                {
                    allSucceeded = false;
                    continue;
                }

                var room = TitleTemplater.ResolveRoomName(_configStore.Load());
                var roomPart = string.IsNullOrWhiteSpace(room) ? string.Empty : $" {room}";
                var title = $"[영상]{roomPart} | {item.Date:yyyy-MM-dd} | {slot.ResolvedSlotLabel}";
                string? audioPath = null;
                try
                {
                    audioPath = await _audioExport.ExportAsync(path, jobId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Error($"{slot.ResolvedSlotLabel} 기존 영상 오디오 자산 생성 실패", ex);
                }

                _assets.Upsert(new PeriodAsset(
                    jobId, item.Date, slot.Number, title, AudioPath: audioPath,
                    SlotId: slot.ResolvedSlotId, SlotLabel: slot.ResolvedSlotLabel,
                    SlotKind: slot.ResolvedSlotKind, SlotOrder: slot.ResolvedSlotOrder));
                _queue.Enqueue(new UploadJob(
                    jobId, path, title, item.Date, slot.Number, UploadJobStatus.Pending, 0, null,
                    slot.ResolvedSlotId, slot.ResolvedSlotLabel,
                    slot.ResolvedSlotKind, slot.ResolvedSlotOrder));
            }

            state.Splits[index] = item with
            {
                Status = allSucceeded ? PendingSplitStatus.Done : PendingSplitStatus.Pending,
                AutoApproveAtLocal = null,
                ExecuteAtLocal = null,
                ResolvedStartLocal = null,
                ResolvedEndLocal = null,
                FailReason = allSucceeded ? null : "고정 시간표 재처리에 실패해 다음 앱 시작 시 다시 시도"
            };
            Persist(state);
        }
    }

    private void Persist(LegacySplitState state)
    {
        var directory = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, _stateFile, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string StableId(string sourceId, DateOnly date, IReadOnlyList<int> periods)
    {
        var material = $"{sourceId}|{date:yyyy-MM-dd}|{string.Join(',', periods)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return "fixed-" + hash[..24];
    }

    private sealed class LegacySplitState
    {
        public List<PendingSplit> Splits { get; set; } = [];
        public MergeChain? Chain { get; set; }
        public JsonElement? LastApproved { get; set; }
    }
}
