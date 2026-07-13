using System.Globalization;
using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Lossless per-period VOD cutter (확장계획서 §4.1). Translates a period's wall-clock window
/// into offsets within the session mp4 and runs <c>ffmpeg -ss .. -i file -t .. -c copy</c>.
/// Because <c>-ss</c> seeks to the nearest preceding keyframe, the start can land up to one GOP
/// early (≤2s with the live encoder's 2s GOP) — acceptable per D2. Reads the session file
/// read-only, so the live pipeline is never touched. Best-effort: a missing/short/locked file
/// yields null + a log warning rather than an exception.
/// </summary>
public sealed class VodSegmentService : IVodSegmentService
{
    private static readonly TimeSpan CutTimeout = TimeSpan.FromMinutes(5);

    private readonly IRecordingSessionInfo _sessionInfo;
    private readonly IProcessRunner _processRunner;
    private readonly ILogService _log;
    private readonly string _outputDir;

    public VodSegmentService(
        IRecordingSessionInfo sessionInfo, IProcessRunner processRunner, ILogService log)
        : this(sessionInfo, processRunner, log, AppPaths.VodDir)
    {
    }

    /// <summary>Test seam: redirect the VOD output directory.</summary>
    public VodSegmentService(
        IRecordingSessionInfo sessionInfo, IProcessRunner processRunner, ILogService log, string outputDir)
    {
        _sessionInfo = sessionInfo;
        _processRunner = processRunner;
        _log = log;
        _outputDir = outputDir;
    }

    public Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct)
    {
        var session = _sessionInfo.Current;
        if (session is null)
        {
            _log.Warn($"{period.PeriodNumber}교시 컷 건너뜀: 진행 중인 세션 녹화가 없습니다.");
            return Task.FromResult<string?>(null);
        }
        return ExtractRangeAsync(session, period.StartLocal, period.EndLocal, $"{period.PeriodNumber}교시", ct);
    }

    public async Task<string?> ExtractRangeAsync(
        RecordingSession session, DateTime startLocal, DateTime endLocal, string fileBaseLabel,
        CancellationToken ct)
    {
        if (!File.Exists(session.FilePath))
        {
            _log.Warn($"{fileBaseLabel} 컷 건너뜀: 세션 파일을 찾을 수 없습니다 ({session.FilePath}).");
            return null;
        }

        // File offsets: position 0 of the mp4 == session.StartLocal. Clamp a window that began
        // before recording started to offset 0 (we only have what was recorded — §4.1 best-effort).
        var startOffset = startLocal - session.StartLocal;
        if (startOffset < TimeSpan.Zero)
        {
            startOffset = TimeSpan.Zero;
        }
        var endOffset = endLocal - session.StartLocal;
        var duration = endOffset - startOffset;
        if (duration <= TimeSpan.Zero)
        {
            _log.Warn($"{fileBaseLabel} 컷 건너뜀: 추출 가능한 구간이 없습니다 " +
                     $"(세션 시작 {session.StartLocal:HH:mm:ss}, 구간 {startLocal:HH:mm:ss}~{endLocal:HH:mm:ss}).");
            return null;
        }

        Directory.CreateDirectory(_outputDir);
        var outputPath = BuildOutputPath(fileBaseLabel, DateOnly.FromDateTime(startLocal));

        var args = string.Join(' ',
            "-hide_banner", "-loglevel warning", "-y",
            // -ss before -i = fast keyframe seek; -t bounds the copied duration. If the file is
            // still being written and shorter than requested, copy stops at EOF (best-effort).
            $"-ss {Seconds(startOffset)}",
            $"-i \"{session.FilePath}\"",
            $"-t {Seconds(duration)}",
            "-c copy", "-avoid_negative_ts make_zero", "-movflags +faststart",
            $"\"{outputPath}\"");

        _log.Info($"{fileBaseLabel} 무손실 컷 시작: +{Seconds(startOffset)}s, {Seconds(duration)}s 길이.");

        try
        {
            var (exitCode, output) = await _processRunner
                .RunAsync(FfmpegLocator.Resolve(), args, CutTimeout, ct).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _log.Warn($"{fileBaseLabel} 컷 실패 (exit={exitCode}). {Tail(output)}");
                TryDelete(outputPath);
                return null;
            }

            _log.Info($"{fileBaseLabel} 컷 완료: {Path.GetFileName(outputPath)} " +
                     $"({new FileInfo(outputPath).Length / (1024.0 * 1024.0):F1} MB)");
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"{fileBaseLabel} 컷 중 오류", ex);
            TryDelete(outputPath);
            return null;
        }
    }

    private string BuildOutputPath(string fileBaseLabel, DateOnly date)
    {
        var baseName = $"{fileBaseLabel}_{date:yyyy-MM-dd}";
        var path = Path.Combine(_outputDir, baseName + ".mp4");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(_outputDir, $"{baseName}_{n}.mp4");
        }
        return path;
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

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
            _log.Debug($"부분 컷 파일 삭제 실패: {ex.Message}");
        }
    }
}
