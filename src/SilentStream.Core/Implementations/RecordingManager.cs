using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Local backup recording bookkeeping (plan §3.6): one file per session named
/// SilentStream_REC_yyyy-MM-dd_HHmm.mp4 in the configured folder, a capacity cap with
/// oldest-first deletion, a 7-day retention window, and a min-free-space threshold.
/// The actual bytes are written by the encoder's tee output; this class only manages files.
/// </summary>
public sealed class RecordingManager : IRecordingManager
{
    private const string FilePrefix = "SilentStream_REC_";
    private const string FilePattern = FilePrefix + "*.mp4";

    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly Func<DateTime> _now;
    private readonly Func<string, long> _freeBytesProvider;

    private string? _currentFilePath;

    public RecordingManager(IConfigStore configStore, ILogService log)
        : this(configStore, log, () => DateTime.Now, GetFreeBytes)
    {
    }

    /// <summary>Test seam: inject clock and free-disk-space provider.</summary>
    public RecordingManager(
        IConfigStore configStore,
        ILogService log,
        Func<DateTime> now,
        Func<string, long> freeBytesProvider)
    {
        _configStore = configStore;
        _log = log;
        _now = now;
        _freeBytesProvider = freeBytesProvider;
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

        _currentFilePath = path;
        return path;
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

    public Task EnforceRetentionAsync(CancellationToken ct)
    {
        var config = _configStore.Load().Recording;
        if (!Directory.Exists(config.Folder))
        {
            return Task.CompletedTask;
        }

        var files = Directory.EnumerateFiles(config.Folder, FilePattern)
            .Select(f => new FileInfo(f))
            .Where(f => !string.Equals(f.FullName, _currentFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.CreationTime)
            .ToList();

        // 1) Retention window: delete files older than retentionDays (creation-time based).
        var cutoff = _now() - TimeSpan.FromDays(config.RetentionDays);
        foreach (var file in files.Where(f => f.CreationTime < cutoff).ToList())
        {
            ct.ThrowIfCancellationRequested();
            Delete(file, $"보존 기간({config.RetentionDays}일) 경과");
            files.Remove(file);
        }

        // 2) Capacity cap: delete oldest-first until under maxSizeGb.
        var maxBytes = (long)config.MaxSizeGb * 1024 * 1024 * 1024;
        var totalBytes = files.Sum(f => f.Length) + CurrentFileLength();
        while (totalBytes > maxBytes && files.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var oldest = files[0];
            files.RemoveAt(0);
            totalBytes -= oldest.Length;
            Delete(oldest, $"용량 한도({config.MaxSizeGb}GB) 초과");
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
            _log.Warn("디스크 여유 공간이 임계치 미만이지만 정리할 이전 녹화 파일이 없습니다. " +
                      "녹화 일시중단이 필요할 수 있습니다(송출은 유지).");
        }

        return Task.CompletedTask;
    }

    private long CurrentFileLength()
    {
        if (_currentFilePath is null || !File.Exists(_currentFilePath))
        {
            return 0;
        }
        return new FileInfo(_currentFilePath).Length;
    }

    private void Delete(FileInfo file, string reason)
    {
        try
        {
            file.Delete();
            _log.Info($"녹화 파일 삭제({reason}): {file.Name}");
        }
        catch (IOException ex)
        {
            _log.Warn($"녹화 파일 삭제 실패: {file.Name} — {ex.Message}");
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
}
