using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using System.Text.Json;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Local backup recording bookkeeping (plan §3.6): one file per session named
/// MediaCaptureHelper_REC_yyyy-MM-dd_HHmm.mp4 in the configured folder, a capacity cap with
/// oldest-first deletion, a 7-day retention window, and a min-free-space threshold.
/// The actual bytes are written by the encoder's tee output; this class only manages files.
/// </summary>
public sealed class RecordingManager : IRecordingManager, IRecordingSessionInfo
{
    private const string FilePrefix = "MediaCaptureHelper_REC_";
    private const string FilePattern = FilePrefix + "*.mp4";

    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly Func<DateTime> _now;
    private readonly Func<string, long> _freeBytesProvider;
    private readonly IRecordingRetentionGuard? _retentionGuard;
    private readonly object _sessionGate = new();
    private readonly List<RecordingSegment> _segments;

    private string? _currentFilePath;
    private DateTime _currentSessionStart;

    public RecordingManager(IConfigStore configStore, ILogService log)
        : this(configStore, log, () => DateTime.Now, GetFreeBytes)
    {
    }

    public RecordingManager(IConfigStore configStore, ILogService log, IRecordingRetentionGuard retentionGuard)
        : this(configStore, log, () => DateTime.Now, GetFreeBytes, retentionGuard) { }

    /// <summary>Test seam: inject clock and free-disk-space provider.</summary>
    public RecordingManager(
        IConfigStore configStore,
        ILogService log,
        Func<DateTime> now,
        Func<string, long> freeBytesProvider,
        IRecordingRetentionGuard? retentionGuard = null)
    {
        _configStore = configStore;
        _log = log;
        _now = now;
        _freeBytesProvider = freeBytesProvider;
        _retentionGuard = retentionGuard;
        _segments = LoadSegmentHistory();
    }

    public string CreateSessionFilePath(DateTime sessionStartLocal)
    {
        var config = _configStore.Load().Recording;
        Directory.CreateDirectory(config.Folder);

        var baseName = $"{FilePrefix}{sessionStartLocal:yyyy-MM-dd_HHmm}";
        var path = Path.Combine(config.Folder, baseName + ".mp4");

        // Same-minute restart (crash + watchdog relaunch): append a part suffix.
        for (var part = 2; File.Exists(path); part++)
        {
            path = Path.Combine(config.Folder, $"{baseName}_part{part}.mp4");
        }

        lock (_sessionGate)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                if (_segments[i].EndLocal is null)
                {
                    _segments[i] = _segments[i] with { EndLocal = LastWrittenEnd(_segments[i], sessionStartLocal) };
                    break;
                }
            }

            _currentFilePath = path;
            _currentSessionStart = sessionStartLocal;
            _segments.Add(new RecordingSegment(path, sessionStartLocal, null));
            PersistSegmentHistoryLocked();
        }
        return path;
    }

    /// <summary>
    /// Current recording session for the VOD cut (확장계획서 §4.1). StartLocal is the value
    /// passed to <see cref="CreateSessionFilePath"/> for the active file, i.e. file offset 0.
    /// </summary>
    public RecordingSession? Current
    {
        get
        {
            lock (_sessionGate)
            {
                return _currentFilePath is null
                    ? null
                    : new RecordingSession(_currentFilePath, _currentSessionStart);
            }
        }
    }

    public IReadOnlyList<RecordingSegment> GetSegments(DateTime startLocal, DateTime endLocal)
    {
        lock (_sessionGate)
        {
            return _segments
                .Where(s => s.StartLocal < endLocal && (s.EndLocal ?? DateTime.MaxValue) > startLocal)
                .OrderBy(s => s.StartLocal)
                .ToList();
        }
    }

    public RecordingStatus GetStatus()
    {
        var config = _configStore.Load().Recording;
        if (!Directory.Exists(config.Folder))
        {
            return new RecordingStatus(_currentFilePath, 0, SafeFreeBytes(config.Folder));
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(config.Folder, FilePattern))
        {
            total += new FileInfo(file).Length;
        }
        return new RecordingStatus(_currentFilePath, total, SafeFreeBytes(config.Folder));
    }

    /// <inheritdoc />
    public long GetActiveRecordingLength() => CurrentFileLength();

    public Task EnforceRetentionAsync(CancellationToken ct)
    {
        var config = _configStore.Load().Recording;
        if (!Directory.Exists(config.Folder))
        {
            return Task.CompletedTask;
        }

        var allFiles = Directory.EnumerateFiles(config.Folder, FilePattern)
            .Select(f => new FileInfo(f))
            .Where(f => !string.Equals(f.FullName, _currentFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.CreationTime)
            .ToList();
        var totalBytes = allFiles.Sum(f => f.Length) + CurrentFileLength();
        var files = allFiles.Where(CanDelete).ToList();
        if (files.Count < allFiles.Count)
            _log.Info($"미업로드/완료 근거 부족 녹화 {allFiles.Count - files.Count}개를 자동 정리에서 보호합니다.");

        // 1) Retention window: delete files older than retentionDays (creation-time based).
        var cutoff = _now() - TimeSpan.FromDays(config.RetentionDays);
        foreach (var file in files.Where(f => f.CreationTime < cutoff).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var length = file.Length;
            if (Delete(file, $"보존 기간({config.RetentionDays}일) 경과")) totalBytes -= length;
            files.Remove(file);
        }

        // 2) Capacity cap: delete oldest-first until under maxSizeGb.
        var maxBytes = (long)config.MaxSizeGb * 1024 * 1024 * 1024;
        while (totalBytes > maxBytes && files.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var oldest = files[0];
            files.RemoveAt(0);
            var length = oldest.Length;
            if (Delete(oldest, $"용량 한도({config.MaxSizeGb}GB) 초과")) totalBytes -= length;
        }

        // 3) Low disk space: clean oldest-first until above minFreeGb.
        var minFreeBytes = (long)config.MinFreeGb * 1024 * 1024 * 1024;
        while (SafeFreeBytes(config.Folder) < minFreeBytes && files.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var oldest = files[0];
            files.RemoveAt(0);
            Delete(oldest, $"디스크 여유 공간 부족(< {config.MinFreeGb}GB)");
        }

        if (SafeFreeBytes(config.Folder) < minFreeBytes)
        {
            _log.Warn("디스크 여유 공간이 부족하지만 안전하게 정리할 녹화가 없습니다. " +
                      "미업로드 원본을 보존합니다. 저장 공간을 확보해야 합니다.");
        }

        return Task.CompletedTask;
    }

    private long CurrentFileLength()
    {
        var path = _currentFilePath; // snapshot: the connect thread may swap it mid-read
        if (path is null)
        {
            return 0;
        }
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient stat failure (file deleted/locked between the checks) must not abort the
            // watchdog tick that samples this — degrade to "no signal" (0) so the stats line alone
            // decides, exactly as a recording-off session does. Retention's use is best-effort too.
            return 0;
        }
    }

    private bool CanDelete(FileInfo file)
    {
        lock (_sessionGate)
        {
            if (string.Equals(file.FullName, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (_retentionGuard is null) return true; // Explicit test/legacy construction only.
            var segment = _segments.LastOrDefault(s => string.Equals(s.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase));
            if (segment is null) return false;
            try { return _retentionGuard.CanDelete(segment); }
            catch (Exception ex) { _log.Warn($"녹화 완료 근거 조회 실패 — 원본 보존: {ex.Message}"); return false; }
        }
    }

    private bool Delete(FileInfo file, string reason)
    {
        try
        {
            lock (_sessionGate)
            {
                if (!CanDelete(file)) return false;
                file.Delete();
            }
            _log.Info($"녹화 파일 삭제({reason}): {file.Name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"녹화 파일 삭제 실패: {file.Name} — {ex.Message}");
            return false;
        }
    }

    private long SafeFreeBytes(string folder)
    {
        try
        {
            return _freeBytesProvider(folder);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            _log.Warn($"디스크 여유 공간 조회 실패: {ex.Message}");
            return long.MaxValue; // 조회 실패가 녹화를 막지 않도록 안전한 쪽으로
        }
    }

    private static long GetFreeBytes(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        return new DriveInfo(root!).AvailableFreeSpace;
    }

    private string SegmentHistoryFile =>
        Path.Combine(_configStore.Load().Recording.Folder, ".recording-segments.json");

    private List<RecordingSegment> LoadSegmentHistory()
    {
        try
        {
            var path = SegmentHistoryFile;
            if (!File.Exists(path))
            {
                return [];
            }

            var history = JsonSerializer.Deserialize<List<RecordingSegment>>(
                       File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? [];
            // An old process cannot have recorded through its downtime until today's reboot.
            return history.Select(s => s with { EndLocal = LastWrittenEnd(s, s.EndLocal) }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Recording segment history could not be read: {ex.Message}");
            return [];
        }
    }

    private static DateTime? LastWrittenEnd(RecordingSegment segment, DateTime? upperBound)
    {
        try
        {
            if (File.Exists(segment.FilePath))
            {
                var written = File.GetLastWriteTime(segment.FilePath);
                if (written >= segment.StartLocal && (upperBound is null || written < upperBound)) return written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return upperBound;
    }

    private void PersistSegmentHistoryLocked()
    {
        var path = SegmentHistoryFile;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(_segments, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Recording segment history could not be saved: {ex.Message}");
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
