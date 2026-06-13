using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// FFmpeg-based encode + tee pipeline (plan §3.5/§3.6): raw BGRA frames from the capture
/// source are written to ffmpeg stdin, mixed PCM from the audio mixer to a named pipe,
/// and the single encoded stream is fanned out to RTMP (onfail=ignore) and the session mp4.
/// Graceful stop closes both inputs (EOF) so ffmpeg finalises the mp4; the fragmented-mp4
/// flags keep the file playable even after a hard kill.
/// </summary>
public sealed class EncoderPipeline : IEncoderPipeline
{
    private const string AudioPipeName = "silentstream_audio";

    private readonly IScreenCaptureSource _capture;
    private readonly IAudioMixer _audioMixer;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly GpuEncoderDetector _detector;

    private Process? _process;
    private NamedPipeServerStream? _audioPipe;
    private Channel<ReadOnlyMemory<byte>>? _videoChannel;
    private Task? _videoWriterTask;
    private CancellationTokenSource? _sessionCts;

    public EncoderPipeline(
        IScreenCaptureSource capture,
        IAudioMixer audioMixer,
        IConfigStore configStore,
        ILogService log,
        IProcessRunner processRunner)
    {
        _capture = capture;
        _audioMixer = audioMixer;
        _configStore = configStore;
        _log = log;
        _detector = new GpuEncoderDetector(processRunner, log);
    }

    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler<MetricsSnapshot>? MetricsUpdated;

    public async Task StartAsync(EncoderStartOptions options, CancellationToken ct)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("인코더가 이미 실행 중입니다.");
        }

        var ffmpegPath = FfmpegLocator.Resolve();
        var config = _configStore.Load();

        var encoder = GpuEncoderExtensions.FromConfigValue(config.Encoding.PreferredGpu)
                      ?? await _detector.DetectAsync(ffmpegPath, ct).ConfigureAwait(false);

        var spec = new EncoderSessionSpec(
            options.Width, options.Height, options.Fps, encoder,
            options.VideoBitrateKbps, options.AudioBitrateKbps,
            options.RtmpUrl, options.RecordingFilePath,
            OperatingSystem.IsWindows() ? @"\\.\pipe\" + AudioPipeName : "/tmp/" + AudioPipeName,
            ResourceLimit: config.Encoding.ResourceLimit);

        var args = FfmpegArgumentsBuilder.Build(spec);
        _log.Info($"FFmpeg 기동: {encoder.ToFfmpegName()}, {options.Width}x{options.Height}@{options.Fps}fps, " +
                  $"{options.VideoBitrateKbps}kbps");
        _log.Debug($"ffmpeg {args}");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sessionToken = _sessionCts.Token;

        // Audio pipe must exist before ffmpeg tries to open it.
        _audioPipe = new NamedPipeServerStream(
            AudioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        _process.ErrorDataReceived += OnFfmpegStderr;
        _process.Exited += (_, _) => _log.Warn("FFmpeg 프로세스가 종료되었습니다.");

        _process.Start();
        try
        {
            // Approximate resource cap: keep encoding below interactive work (plan §3.5).
            _process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            _log.Debug($"프로세스 우선순위 설정 실패: {ex.Message}");
        }
        _process.BeginErrorReadLine();

        // Bounded frame queue: if ffmpeg falls behind we drop the oldest frame instead
        // of stalling the capture thread.
        _videoChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _capture.FrameCaptured += OnFrameCaptured;
        _audioMixer.SamplesAvailable += OnSamplesAvailable;

        _videoWriterTask = PumpVideoAsync(_process.StandardInput.BaseStream, sessionToken);
        _ = WaitForAudioConnectionAsync(sessionToken);
    }

    public async Task StopAsync()
    {
        _capture.FrameCaptured -= OnFrameCaptured;
        _audioMixer.SamplesAvailable -= OnSamplesAvailable;
        _sessionCts?.Cancel();
        _videoChannel?.Writer.TryComplete();

        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (_videoWriterTask is not null)
            {
                await _videoWriterTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        try
        {
            // EOF on both inputs tells ffmpeg to flush and finalise the mp4 moov fragments.
            process.StandardInput.BaseStream.Close();
            _audioPipe?.Dispose();

            if (!process.HasExited && !await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            {
                _log.Warn("FFmpeg가 제한 시간 내 종료되지 않아 강제 종료합니다(frag mp4라 파일은 안전).");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            process.Dispose();
            _process = null;
            _audioPipe = null;
        }
        _log.Info("인코더 파이프라인이 정지되었습니다.");
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort cleanup on dispose.
        }
        _sessionCts?.Dispose();
    }

    private void OnFrameCaptured(object? sender, VideoFrame frame) =>
        _videoChannel?.Writer.TryWrite(frame.Data);

    private async void OnSamplesAvailable(object? sender, AudioBuffer buffer)
    {
        var pipe = _audioPipe;
        if (pipe is not { IsConnected: true })
        {
            return;
        }
        try
        {
            await pipe.WriteAsync(buffer.Pcm).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.Debug($"오디오 파이프 쓰기 실패: {ex.Message}");
        }
    }

    private async Task PumpVideoAsync(Stream stdin, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _videoChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await stdin.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Session ending or ffmpeg gone; the watchdog (Phase 5) handles restarts.
        }
    }

    private async Task WaitForAudioConnectionAsync(CancellationToken ct)
    {
        try
        {
            await _audioPipe!.WaitForConnectionAsync(ct).ConfigureAwait(false);
            _log.Debug("FFmpeg가 오디오 파이프에 연결되었습니다.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private void OnFfmpegStderr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        var metrics = FfmpegProgressParser.TryParse(e.Data, DateTime.UtcNow);
        if (metrics is not null)
        {
            MetricsUpdated?.Invoke(this, metrics);
        }
        else if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"FFmpeg: {e.Data}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
