using SilentStream.Core.Contracts;

namespace SilentStream.Core.Media;

/// <summary>
/// Detects the best available H.264 encoder (plan §3.5): lists ffmpeg encoders, then
/// confirms each candidate with a real 1-frame test encode, falling back
/// NVENC → AMF → QSV → x264. x264 is always considered available.
/// </summary>
public sealed class GpuEncoderDetector(IProcessRunner runner, ILogService log)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private static readonly GpuEncoder[] Priority =
        [GpuEncoder.Nvenc, GpuEncoder.Amf, GpuEncoder.Qsv, GpuEncoder.X264];

    public async Task<GpuEncoder> DetectAsync(string ffmpegPath, CancellationToken ct)
    {
        string encoderList;
        try
        {
            var (exitCode, output) = await runner
                .RunAsync(ffmpegPath, "-hide_banner -encoders", ProbeTimeout, ct)
                .ConfigureAwait(false);
            encoderList = exitCode == 0 ? output : string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("FFmpeg 인코더 목록 조회 실패 — x264로 폴백합니다.", ex);
            return GpuEncoder.X264;
        }

        foreach (var candidate in Priority)
        {
            if (candidate != GpuEncoder.X264 &&
                !encoderList.Contains(candidate.ToFfmpegName(), StringComparison.Ordinal))
            {
                continue;
            }

            if (await ProbeAsync(ffmpegPath, candidate, ct).ConfigureAwait(false))
            {
                log.Info($"하드웨어 인코더 감지: {candidate.ToFfmpegName()}");
                return candidate;
            }
        }

        log.Warn("사용 가능한 하드웨어 인코더가 없어 x264(CPU)로 폴백합니다.");
        return GpuEncoder.X264;
    }

    /// <summary>One-frame test encode: listed encoders can still fail without the GPU/driver.</summary>
    private async Task<bool> ProbeAsync(string ffmpegPath, GpuEncoder encoder, CancellationToken ct)
    {
        var args = $"-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:r=30 " +
                   $"-frames:v 1 -c:v {encoder.ToFfmpegName()} -f null -";
        try
        {
            var (exitCode, _) = await runner
                .RunAsync(ffmpegPath, args, ProbeTimeout, ct)
                .ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Debug($"{encoder.ToFfmpegName()} 테스트 인코딩 실패: {ex.Message}");
            return false;
        }
    }
}
