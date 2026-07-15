using System.ComponentModel;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Restores a missing local period-audio asset from the app-owned public/unlisted YouTube VOD
/// already linked in <see cref="IPeriodAssetCatalog"/>. Jobs are background, per-asset
/// deduplicated, and globally serialized so recovery cannot create a burst of network/CPU load.
/// </summary>
public sealed class YouTubeAudioRecoveryService : IYouTubeAudioRecoveryService, IDisposable
{
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(24);
    private static readonly Regex AssetIdPattern = new(
        "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VideoIdPattern = new(
        "^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IProcessRunner _processRunner;
    private readonly ILocalAudioExportService _audioExport;
    private readonly IPeriodAssetCatalog _assets;
    private readonly ILogService _log;
    private readonly string _audioDirectory;
    private readonly string _temporaryRoot;
    private readonly string _ytDlpPath;
    private readonly TimeSpan _downloadTimeout;
    private readonly Func<DateTime> _utcNow;
    private readonly object _jobsGate = new();
    private readonly Dictionary<string, RecoveryJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public YouTubeAudioRecoveryService(
        IProcessRunner processRunner,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log)
        : this(
            processRunner,
            audioExport,
            assets,
            log,
            AppPaths.PeriodAudioDir,
            AppPaths.PeriodAudioRecoveryTempDir,
            YtDlpLocator.Resolve(),
            DefaultDownloadTimeout)
    {
    }

    /// <summary>Test seam for redirecting files, executable resolution, time, and timeout.</summary>
    public YouTubeAudioRecoveryService(
        IProcessRunner processRunner,
        ILocalAudioExportService audioExport,
        IPeriodAssetCatalog assets,
        ILogService log,
        string audioDirectory,
        string temporaryRoot,
        string ytDlpPath,
        TimeSpan downloadTimeout,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(audioExport);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ytDlpPath);
        if (downloadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        }

        _processRunner = processRunner;
        _audioExport = audioExport;
        _assets = assets;
        _log = log;
        _audioDirectory = Path.GetFullPath(audioDirectory);
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _ytDlpPath = ytDlpPath;
        _downloadTimeout = downloadTimeout;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        CleanupStaleTemporaryDirectories();
    }

    public AudioRecoverySnapshot Start(string assetId)
    {
        var normalizedAssetId = assetId ?? string.Empty;
        if (!AssetIdPattern.IsMatch(normalizedAssetId))
        {
            return Failed(normalizedAssetId, "교시 음성 자산 ID가 올바르지 않습니다.");
        }

        PeriodAsset? asset;
        try
        {
            asset = _assets.Find(normalizedAssetId);
        }
        catch (Exception ex)
        {
            _log.Error("YouTube 음성 복구 자산 조회 실패", ex);
            return Failed(normalizedAssetId, "교시 음성 정보를 읽지 못했습니다.");
        }

        if (asset is null)
        {
            return Failed(normalizedAssetId, "교시 자료를 찾을 수 없습니다.");
        }
        if (IsCached(asset.AudioPath))
        {
            return new AudioRecoverySnapshot(normalizedAssetId, AudioRecoveryStatus.Available);
        }
        if (!VideoIdPattern.IsMatch(asset.VideoId ?? string.Empty))
        {
            return Failed(normalizedAssetId, "복구할 YouTube 영상 정보가 없습니다.");
        }

        lock (_jobsGate)
        {
            if (_disposed)
            {
                return Failed(normalizedAssetId, "앱이 종료 중이라 음성을 복구할 수 없습니다.");
            }

            if (_jobs.TryGetValue(normalizedAssetId, out var existing) &&
                AudioRecoveryStatus.IsActive(existing.Snapshot.Status))
            {
                return existing.Snapshot;
            }

            var job = new RecoveryJob(
                new AudioRecoverySnapshot(normalizedAssetId, AudioRecoveryStatus.Queued,
                    "YouTube 음성 복구를 기다리고 있습니다."));
            _jobs[normalizedAssetId] = job;
            job.Task = Task.Run(() => RunRecoveryAsync(job, asset, _shutdown.Token));
            return job.Snapshot;
        }
    }

    public AudioRecoverySnapshot GetStatus(string assetId)
    {
        var normalizedAssetId = assetId ?? string.Empty;
        if (!AssetIdPattern.IsMatch(normalizedAssetId))
        {
            return new AudioRecoverySnapshot(normalizedAssetId, AudioRecoveryStatus.Idle);
        }

        try
        {
            if (_assets.Find(normalizedAssetId) is { } asset && IsCached(asset.AudioPath))
            {
                return new AudioRecoverySnapshot(normalizedAssetId, AudioRecoveryStatus.Available);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"YouTube 음성 복구 상태의 캐시 확인 실패: {ex.Message}");
        }

        lock (_jobsGate)
        {
            return _jobs.TryGetValue(normalizedAssetId, out var job)
                ? job.Snapshot
                : new AudioRecoverySnapshot(normalizedAssetId, AudioRecoveryStatus.Idle);
        }
    }

    private async Task RunRecoveryAsync(RecoveryJob job, PeriodAsset asset, CancellationToken ct)
    {
        var enteredExecutionGate = false;
        string? temporaryDirectory = null;
        string? completedAudioPath = null;
        var catalogCommitted = false;
        var encodingStarted = false;
        var recoveryCompleted = false;

        try
        {
            await _executionGate.WaitAsync(ct).ConfigureAwait(false);
            enteredExecutionGate = true;
            SetStatus(job, AudioRecoveryStatus.Downloading, "YouTube에서 음성을 받는 중입니다.");

            temporaryDirectory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var outputTemplate = Path.Combine(temporaryDirectory, "source.%(ext)s");
            var watchUrl = $"https://www.youtube.com/watch?v={asset.VideoId}";
            var arguments = string.Join(' ',
                "--ignore-config",
                "--no-playlist",
                "--quiet",
                "--no-warnings",
                "--no-progress",
                "--format bestaudio",
                $"--output \"{outputTemplate}\"",
                $"-- \"{watchUrl}\"");

            var (exitCode, output) = await _processRunner
                .RunAsync(_ytDlpPath, arguments, _downloadTimeout, ct)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                var message = ClassifyDownloadFailure(output);
                _log.Warn($"YouTube 음성 복구 다운로드 실패(asset={asset.Id}, exit={exitCode}): {Tail(output)}");
                SetStatus(job, AudioRecoveryStatus.Failed, message);
                return;
            }

            var sourcePath = FindDownloadedSource(temporaryDirectory);
            if (sourcePath is null)
            {
                SetStatus(job, AudioRecoveryStatus.Failed,
                    "YouTube에서 받은 음성 파일이 비어 있거나 형식이 올바르지 않습니다.");
                return;
            }

            SetStatus(job, AudioRecoveryStatus.Encoding, "다운로드용 음성 파일을 만드는 중입니다.");
            encodingStarted = true;
            completedAudioPath = await _audioExport.ExportAsync(sourcePath, asset.Id, ct).ConfigureAwait(false);
            if (!IsCached(completedAudioPath))
            {
                SetStatus(job, AudioRecoveryStatus.Failed,
                    "YouTube 음성을 다운로드용 파일로 변환하지 못했습니다.");
                return;
            }

            if (!_assets.MarkAudioPath(asset.Id, completedAudioPath!))
            {
                SetStatus(job, AudioRecoveryStatus.Failed, "복구한 음성 파일 정보를 저장하지 못했습니다.");
                return;
            }

            catalogCommitted = true;
            _log.Info($"YouTube 음성 복구 완료: asset={asset.Id}, file={Path.GetFileName(completedAudioPath)}");
            recoveryCompleted = true;
        }
        catch (OperationCanceledException)
        {
            var message = _shutdown.IsCancellationRequested
                ? "앱 종료로 YouTube 음성 복구가 중단되었습니다."
                : encodingStarted
                    ? "다운로드한 음성 파일의 변환 시간이 초과되었습니다."
                    : $"YouTube 음성 복구가 {_downloadTimeout.TotalMinutes:0}분 제한시간을 초과했습니다.";
            SetStatus(job, AudioRecoveryStatus.Failed, message);
        }
        catch (Win32Exception ex)
        {
            _log.Warn($"YouTube 음성 복구 도구 실행 실패: {ex.Message}");
            SetStatus(job, AudioRecoveryStatus.Failed,
                "YouTube 음성 복구 도구가 설치되어 있지 않거나 실행할 수 없습니다.");
        }
        catch (Exception ex)
        {
            _log.Error("YouTube 음성 복구 중 오류", ex);
            SetStatus(job, AudioRecoveryStatus.Failed,
                "YouTube 음성 복구 중 오류가 발생했습니다. 잠시 후 다시 시도해주세요.");
        }
        finally
        {
            if (!catalogCommitted && completedAudioPath is not null)
            {
                TryDeleteFile(completedAudioPath);
            }
            if (temporaryDirectory is not null)
            {
                TryDeleteTemporaryDirectory(temporaryDirectory);
            }
            // Available is externally observable. Publish it only after the per-job temporary
            // directory has been removed so callers never see a terminal state with cleanup
            // still pending (the race is especially visible on Linux runners).
            if (recoveryCompleted)
            {
                SetStatus(job, AudioRecoveryStatus.Available);
            }
            if (enteredExecutionGate)
            {
                _executionGate.Release();
            }
        }
    }

    private void SetStatus(RecoveryJob job, string status, string? message = null)
    {
        lock (_jobsGate)
        {
            if (_jobs.TryGetValue(job.Snapshot.AssetId, out var current) && ReferenceEquals(current, job))
            {
                job.Snapshot = new AudioRecoverySnapshot(job.Snapshot.AssetId, status, message);
            }
        }
    }

    private bool IsCached(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(_audioDirectory) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(candidate);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetExtension(fullPath), ".m4a", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(fullPath) &&
                   new FileInfo(fullPath).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string? FindDownloadedSource(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory)
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Where(path => new FileInfo(path).Length > 0)
                .Take(2)
                .ToArray();
            return files.Length == 1 ? files[0] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ClassifyDownloadFailure(string output)
    {
        if (Contains(output, "private video") || Contains(output, "video is private"))
        {
            return "YouTube 영상이 비공개여서 음성을 복구할 수 없습니다.";
        }
        if (Contains(output, "processing") || Contains(output, "not yet available"))
        {
            return "YouTube 영상 처리가 아직 끝나지 않았습니다. 잠시 후 다시 시도해주세요.";
        }
        if (Contains(output, "video unavailable") || Contains(output, "video has been removed") ||
            Contains(output, "this video is unavailable"))
        {
            return "YouTube 영상이 삭제되었거나 사용할 수 없어 음성을 복구할 수 없습니다.";
        }
        if (Contains(output, "sign in") || Contains(output, "confirm your age"))
        {
            return "로그인이 필요한 YouTube 영상은 음성을 복구할 수 없습니다.";
        }
        return "YouTube에서 음성을 받지 못했습니다. 잠시 후 다시 시도해주세요.";
    }

    private static bool Contains(string value, string text) =>
        value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }

    private void CleanupStaleTemporaryDirectories()
    {
        if (!Directory.Exists(_temporaryRoot))
        {
            return;
        }

        try
        {
            var cutoff = _utcNow() - StaleTemporaryAge;
            foreach (var directory in Directory.EnumerateDirectories(_temporaryRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        TryDeleteTemporaryDirectory(directory);
                    }
                }
                catch (IOException ex)
                {
                    _log.Debug($"오래된 YouTube 음성 임시 폴더 확인 실패: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"YouTube 음성 임시 폴더 정리 실패: {ex.Message}");
        }
    }

    private void TryDeleteTemporaryDirectory(string directory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(directory);
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"YouTube 음성 임시 폴더 삭제 실패: {ex.Message}");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (IsPathInside(_audioDirectory, path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug($"실패한 YouTube 음성 파일 삭제 실패: {ex.Message}");
        }
    }

    private static bool IsPathInside(string rootDirectory, string candidate)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) +
                       Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static AudioRecoverySnapshot Failed(string assetId, string message) =>
        new(assetId, AudioRecoveryStatus.Failed, message);

    public void Dispose()
    {
        lock (_jobsGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed class RecoveryJob(AudioRecoverySnapshot snapshot)
    {
        public AudioRecoverySnapshot Snapshot { get; set; } = snapshot;
        public Task? Task { get; set; }
    }
}
