using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// JSON-backed, process-local thread-safe period-asset catalog. Every mutation is persisted by
/// atomically replacing the catalog file with a same-directory temporary file, so a process crash
/// cannot leave a partially-written primary catalog.
/// </summary>
public sealed class PeriodAssetCatalog : IPeriodAssetCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _catalogFile;
    private readonly Func<DateTime> _utcNow;
    private CatalogState? _state;

    /// <summary>Uses the app's standard durable period-asset catalog location.</summary>
    public PeriodAssetCatalog() : this(AppPaths.PeriodAssetsFile)
    {
    }

    /// <summary>
    /// Creates a catalog at <paramref name="catalogFile"/>. <paramref name="utcNow"/> is an
    /// optional test seam and should return UTC time in production callers that supply it.
    /// </summary>
    public PeriodAssetCatalog(string catalogFile, Func<DateTime>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(catalogFile))
        {
            throw new ArgumentException("A period asset catalog path is required.", nameof(catalogFile));
        }

        _catalogFile = Path.GetFullPath(catalogFile);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public PeriodAsset Upsert(PeriodAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var stamped = NormalizeForSave(asset);

        lock (_gate)
        {
            var state = LoadLocked();
            var index = FindIndex(state.Assets, stamped.Id);
            if (index >= 0)
            {
                var existing = state.Assets[index];
                if (!string.IsNullOrWhiteSpace(existing.VideoId))
                    stamped = stamped with { VideoId = existing.VideoId, SourceKey = existing.SourceKey,
                        UploadedFileSizeBytes = existing.UploadedFileSizeBytes,
                        UploadValidationVersion = existing.UploadValidationVersion };
                state.Assets[index] = stamped;
            }
            else
            {
                state.Assets.Add(stamped);
            }
            PersistLocked();
            return stamped;
        }
    }

    public IReadOnlyList<PeriodAsset> Snapshot()
    {
        lock (_gate)
        {
            return LoadLocked().Assets
                .OrderByDescending(asset => asset.UpdatedAtUtc)
                .ThenByDescending(asset => asset.Date)
                .ThenByDescending(asset => asset.ResolvedSlotOrder)
                .ThenBy(asset => asset.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public PeriodAsset? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            return LoadLocked().Assets.FirstOrDefault(asset =>
                string.Equals(asset.Id, id, StringComparison.Ordinal));
        }
    }

    public bool MarkUploaded(string id, string videoId) => MarkUploaded(id, videoId, null, 0);

    public bool MarkUploaded(string id, string videoId, long? fileSizeBytes, int validationVersion)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedVideoId = RequireValue(videoId, nameof(videoId));

        lock (_gate)
        {
            var state = LoadLocked();
            var index = FindIndex(state.Assets, normalizedId);
            if (index < 0)
            {
                return false;
            }

            state.Assets[index] = state.Assets[index] with
            {
                VideoId = normalizedVideoId,
                UploadedFileSizeBytes = fileSizeBytes ?? state.Assets[index].UploadedFileSizeBytes,
                UploadValidationVersion = Math.Max(validationVersion, state.Assets[index].UploadValidationVersion),
                UpdatedAtUtc = UtcNow()
            };
            PersistLocked();
            return true;
        }
    }

    public bool MarkAudioPath(string id, string audioPath)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedPath = RequireValue(audioPath, nameof(audioPath));

        lock (_gate)
        {
            var state = LoadLocked();
            var index = FindIndex(state.Assets, normalizedId);
            if (index < 0)
            {
                return false;
            }

            state.Assets[index] = state.Assets[index] with
            {
                AudioPath = normalizedPath,
                UpdatedAtUtc = UtcNow()
            };
            PersistLocked();
            return true;
        }
    }

    public bool MarkCaptionStatus(string id, string status, string? language = null, string? message = null)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedStatus = RequireValue(status, nameof(status));

        lock (_gate)
        {
            var state = LoadLocked();
            var index = FindIndex(state.Assets, normalizedId);
            if (index < 0)
            {
                return false;
            }

            state.Assets[index] = state.Assets[index] with
            {
                CaptionStatus = normalizedStatus,
                CaptionLanguage = NormalizeOptional(language),
                CaptionMessage = NormalizeOptional(message),
                UpdatedAtUtc = UtcNow()
            };
            PersistLocked();
            return true;
        }
    }

    private CatalogState LoadLocked()
    {
        if (_state is not null)
        {
            return _state;
        }

        if (!File.Exists(_catalogFile))
        {
            return _state = new CatalogState();
        }

        try
        {
            var json = File.ReadAllText(_catalogFile);
            _state = JsonSerializer.Deserialize<CatalogState>(json, JsonOptions)
                ?? throw new JsonException("Asset catalog is null.");
            if (_state.Assets is null) { _state = null; throw new JsonException("Assets are null."); }
            return _state;
        }
        catch (JsonException ex)
        {
            BackupMalformedCatalog();
            _state = null;
            throw new InvalidDataException("영상 영수증 목록이 손상되었습니다. 복구 전 재업로드/원본 정리를 보류합니다.", ex);
        }
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_catalogFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Same-directory replacement keeps the successful write atomic on the normal local
        // filesystem, while a leftover temp file is harmless and cleaned up on the next mutation.
        var temporaryFile = _catalogFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryFile, _catalogFile, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryFile);
        }
    }

    private PeriodAsset NormalizeForSave(PeriodAsset asset)
    {
        var status = string.IsNullOrWhiteSpace(asset.CaptionStatus)
            ? PeriodAssetCaptionStatus.Pending
            : asset.CaptionStatus.Trim();

        return asset with
        {
            Id = RequireValue(asset.Id, nameof(asset.Id)),
            PeriodNumber = NormalizePeriodNumber(asset),
            Title = RequireValue(asset.Title, nameof(asset.Title)),
            AudioPath = NormalizeOptional(asset.AudioPath),
            VideoId = NormalizeOptional(asset.VideoId),
            CaptionStatus = status,
            CaptionLanguage = NormalizeOptional(asset.CaptionLanguage),
            CaptionMessage = NormalizeOptional(asset.CaptionMessage),
            SlotId = asset.ResolvedSlotId,
            SlotLabel = asset.ResolvedSlotLabel,
            SlotKind = asset.ResolvedSlotKind,
            SlotOrder = asset.ResolvedSlotOrder,
            UpdatedAtUtc = UtcNow()
        };
    }

    private static int NormalizePeriodNumber(PeriodAsset asset)
    {
        if (asset.ResolvedSlotKind == ScheduleSlotKinds.Lunch)
        {
            return 0;
        }
        return RequirePositive(asset.PeriodNumber, nameof(asset.PeriodNumber));
    }

    private DateTime UtcNow()
    {
        var now = _utcNow();
        return now.Kind switch
        {
            DateTimeKind.Utc => now,
            DateTimeKind.Local => now.ToUniversalTime(),
            _ => DateTime.SpecifyKind(now, DateTimeKind.Utc)
        };
    }

    private void BackupMalformedCatalog()
    {
        try
        {
            File.Copy(_catalogFile, _catalogFile + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // A malformed catalog should not block a fresh catalog from being created later.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original file when possible; callers can still recover by upserting.
        }
    }

    private static int FindIndex(List<PeriodAsset> assets, string id) =>
        assets.FindIndex(asset => string.Equals(asset.Id, id, StringComparison.Ordinal));

    private static string RequireValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
        return value.Trim();
    }

    private static int RequirePositive(int value, string parameterName)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Period number must be at least 1.");
        }
        return value;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort only: the primary catalog has already been written or remains intact.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only: the primary catalog has already been written or remains intact.
        }
    }

    private sealed class CatalogState
    {
        public List<PeriodAsset> Assets { get; set; } = [];
    }
}
