using SilentStream.Core.Contracts;
using SilentStream.Core.Media;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Produces a compact, durable M4A from an already-cut, locally-recorded period VOD. Audio is
/// re-encoded as 48 kbps mono AAC, which suits spoken lessons while keeping phone downloads small.
/// This runs only after a period has been cut, so it does not affect the live encoder.
/// </summary>
public sealed class LocalAudioExportService : ILocalAudioExportService
{
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(5);
    private const int DownloadAudioBitrateKbps = 48;

    private readonly IProcessRunner _processRunner;
    private readonly ILogService _log;
    private readonly string _outputDir;

    public LocalAudioExportService(IProcessRunner processRunner, ILogService log)
        : this(processRunner, log, AppPaths.PeriodAudioDir)
    {
    }

    /// <summary>Test seam: redirects generated M4A files to <paramref name="outputDir"/>.</summary>
    public LocalAudioExportService(IProcessRunner processRunner, ILogService log, string outputDir)
    {
        _processRunner = processRunner;
        _log = log;
        _outputDir = outputDir;
    }

    public async Task<string?> ExportAsync(string sourceMediaPath, string assetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assetId) || !File.Exists(sourceMediaPath))
        {
            _log.Warn("교시 음성 내보내기를 건너뜁니다. 원본 영상 또는 자산 ID가 없습니다.");
            return null;
        }

        Directory.CreateDirectory(_outputDir);
        var outputPath = Path.Combine(_outputDir, assetId + ".m4a");
        TryDelete(outputPath);

        var args = string.Join(' ',
            "-hide_banner", "-loglevel warning", "-y",
            $"-i \"{sourceMediaPath}\"",
            // Map only the first local audio stream. Mono 48 kbps AAC is about 22 MB per hour,
            // versus about 72 MB per hour for the 160 kbps source stream.
            "-map 0:a:0", "-vn", "-c:a aac", $"-b:a {DownloadAudioBitrateKbps}k", "-ac 1",
            "-movflags +faststart",
            $"\"{outputPath}\"");

        try
        {
            var (exitCode, output) = await _processRunner
                .RunAsync(FfmpegLocator.Resolve(), args, ExportTimeout, ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _log.Warn($"교시 음성 내보내기 실패(exit={exitCode}). {Tail(output)}");
                TryDelete(outputPath);
                return null;
            }

            _log.Info($"교시 음성 내보내기 완료: {Path.GetFileName(outputPath)}");
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("교시 음성 내보내기 중 오류", ex);
            TryDelete(outputPath);
            return null;
        }
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _log.Debug($"부분 음성 파일 정리 실패: {ex.Message}");
        }
    }
}
