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
    private const string AudioPipeName = "mediacapturehelper_audio";

    private readonly IScreenCaptureSource _capture;
    private readonly IAudioMixer _audioMixer;
    private readonly IConfigStore _configStore;
    private readonly ILogService _log;
    private readonly GpuEncoderDetector _detector;

    private Process? _process;
    private NamedPipeServerStream? _audioPipe;
    private Channel<ReadOnlyMemory<byte>>? _videoChannel;
    private Task? _videoWriterTask;
    private Channel<byte[]>? _audioChannel;
    private Task? _audioWriterTask;
    private CancellationTokenSource? _sessionCts;
    private volatile bool _intendedRunning;
    private DateTime _lastProgressUtc;
    // Per-session signal state (reset in StartAsync before the process spawns): the delta
    // windower for the cumulative stats counters, the fifo drop counter (NET congestion signal),
    // the sticky RTMP-leg-failure flag, and the ffmpeg child-process CPU sample baseline.
    private MetricsWindower? _windower;
    private int _teeDropCount;
    private int _rtmpLegDown;
    private TimeSpan _lastEncoderCpuTime;
    private DateTime _lastEncoderCpuSampleUtc;

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

    public TimeSpan TimeSinceProgress =>
        _intendedRunning ? DateTime.UtcNow - _lastProgressUtc : TimeSpan.Zero;

    public bool RtmpLegDown => Volatile.Read(ref _rtmpLegDown) == 1;

    public event EventHandler<MetricsSnapshot>? MetricsUpdated;

    public event EventHandler? UnexpectedExit;

    public async Task StartAsync(EncoderStartOptions options, CancellationToken ct)
    {
        // A cancelled session token must never spawn a fresh ffmpeg: without this check a stop
        // sequence racing a pipeline swap (방송만 중지 vs 전체 중지) can start an orphan encoder
        // AFTER the teardown finished — nothing supervises or stops it until the next session.
        // (With PreferredGpu configured the DetectAsync call below is skipped, so ct would
        // otherwise go completely unobserved on this path.)
        ct.ThrowIfCancellationRequested();

        // Always tear down any prior session first. When ffmpeg is killed externally (the exact
        // situation the watchdog restarts from), StopAsync is never called, so the previous
        // NamedPipeServerStream is still open and a fresh `new NamedPipeServerStream(..., maxInstances:1)`
        // below would throw IOException "all pipe instances are busy" — leaving recovery stuck.
        await CleanupSessionAsync().ConfigureAwait(false);

        var ffmpegPath = FfmpegLocator.Resolve();
        var config = _configStore.Load();

        var encoder = GpuEncoderExtensions.FromConfigValue(config.Encoding.PreferredGpu)
                      ?? await _detector.DetectAsync(ffmpegPath, ct).ConfigureAwait(false);

        var spec = new EncoderSessionSpec(
            options.Width, options.Height, options.Fps, encoder,
            options.VideoBitrateKbps, options.AudioBitrateKbps,
            options.RtmpUrl, options.RecordingFilePath,
            OperatingSystem.IsWindows() ? @"\\.\pipe\" + AudioPipeName : "/tmp/" + AudioPipeName,
            ResourceLimit: config.Encoding.ResourceLimit,
            AudioFilters: options.AudioFilters,
            OutputWidth: options.OutputWidth,
            OutputHeight: options.OutputHeight,
            OutputFps: options.OutputFps);

        var args = FfmpegArgumentsBuilder.Build(spec);
        _log.Info($"FFmpeg 기동: {encoder.ToFfmpegName()}, {options.Width}x{options.Height}@{options.Fps}fps, " +
                  $"{options.VideoBitrateKbps}kbps");
        _log.Debug($"ffmpeg {args}");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sessionToken = _sessionCts.Token;

        // Fresh per-session signal state BEFORE the process spawns — stderr events may fire as
        // soon as BeginErrorReadLine is called.
        _windower = new MetricsWindower();
        Interlocked.Exchange(ref _teeDropCount, 0);
        Volatile.Write(ref _rtmpLegDown, 0);
        _lastEncoderCpuTime = default;
        _lastEncoderCpuSampleUtc = default;

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
        _process.Exited += (_, _) =>
        {
            // Distinguish a crash/external kill (we still want to be running → watchdog recovers)
            // from a graceful stop (StopAsync cleared the flag first → nothing to do).
            if (_intendedRunning)
            {
                _log.Warn("FFmpeg가 예기치 않게 종료되었습니다 — 워치독이 복구합니다.");
                UnexpectedExit?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _log.Warn("FFmpeg 프로세스가 종료되었습니다.");
            }
        };

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

        // Audio writes go through a single task: NamedPipeServerStream does not support
        // concurrent overlapped WriteAsync calls. Firing one write per SamplesAvailable event
        // (async void) overlaps them as soon as ffmpeg stops draining the pipe (e.g. after an
        // RTMP slave failure stalls the tee), which corrupts the overlapped-I/O state and
        // crashes the process with c0000005. One reader, drop-oldest under backpressure.
        _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _capture.FrameCaptured += OnFrameCaptured;
        _audioMixer.SamplesAvailable += OnSamplesAvailable;

        _videoWriterTask = PumpVideoAsync(_process.StandardInput.BaseStream, sessionToken);
        _audioWriterTask = PumpAudioAsync(sessionToken);

        // From here on, an ffmpeg exit is unexpected (handled by the watchdog).
        _lastProgressUtc = DateTime.UtcNow;
        _intendedRunning = true;
    }

    public async Task StopAsync()
    {
        // Clear intent first so the Exited handler treats this as a graceful stop, not a crash.
        _intendedRunning = false;
        await CleanupSessionAsync().ConfigureAwait(false);
        _log.Info("인코더 파이프라인이 정지되었습니다.");
    }

    /// <summary>
    /// Idempotent, null-safe teardown of the current session (process, named pipe, pump tasks,
    /// channels, CTS). Safe to call when nothing is running. Invoked both by <see cref="StopAsync"/>
    /// and at the top of <see cref="StartAsync"/> so a restart never collides with a leaked pipe.
    /// </summary>
    private async Task CleanupSessionAsync()
    {
        _intendedRunning = false;
        _capture.FrameCaptured -= OnFrameCaptured;
        _audioMixer.SamplesAvailable -= OnSamplesAvailable;
        _sessionCts?.Cancel();
        _videoChannel?.Writer.TryComplete();
        _audioChannel?.Writer.TryComplete();

        // Drain the writer tasks before touching the streams they write to.
        try
        {
            if (_videoWriterTask is not null)
            {
                await _videoWriterTask.ConfigureAwait(false);
            }
            if (_audioWriterTask is not null)
            {
                await _audioWriterTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        _videoWriterTask = null;
        _audioWriterTask = null;

        var process = _process;
        if (process is not null)
        {
            try
            {
                // EOF on both inputs tells ffmpeg to flush and finalise the mp4 moov fragments.
                process.StandardInput.BaseStream.Close();

                if (!process.HasExited &&
                    !await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                {
                    _log.Warn("FFmpeg가 제한 시간 내 종료되지 않아 강제 종료합니다(frag mp4라 파일은 안전).");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // Process already exited / stream already closed.
            }
            process.Dispose();
            _process = null;
        }

        // Always release the named pipe server so the next StartAsync can recreate it
        // (without this, an external kill leaves it open → ERROR_PIPE_BUSY on restart).
        _audioPipe?.Dispose();
        _audioPipe = null;

        _sessionCts?.Dispose();
        _sessionCts = null;
        _videoChannel = null;
        _audioChannel = null;
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

    private void OnSamplesAvailable(object? sender, AudioBuffer buffer) =>
        // Copy off the mixer's reused PCM buffer; the single PumpAudioAsync task does the write.
        _audioChannel?.Writer.TryWrite(buffer.Pcm.ToArray());

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

    private async Task PumpAudioAsync(CancellationToken ct)
    {
        var pipe = _audioPipe;
        if (pipe is null)
        {
            return;
        }
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            _log.Debug("FFmpeg가 오디오 파이프에 연결되었습니다.");

            // Single-threaded drain: exactly one outstanding pipe write at a time.
            await foreach (var chunk in _audioChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await pipe.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Session ending or ffmpeg gone; the watchdog (Phase 5) handles restarts.
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
            _lastProgressUtc = DateTime.UtcNow; // feed is alive; resets the stall watchdog
            var windowed = _windower?.Apply(metrics) ?? metrics;
            MetricsUpdated?.Invoke(this, windowed with
            {
                TeeDropCount = Volatile.Read(ref _teeDropCount),
                EncoderCpuPercent = SampleEncoderCpuPercent()
            });
            return;
        }

        if (FfmpegProgressParser.IsTeeDropLine(e.Data))
        {
            // Uplink congestion: drop_pkts_on_overflow discarded RTMP packets. Counted rather
            // than just logged — the adaptive controller reads the cumulative count off the
            // metrics stream. Drops arrive in bursts, so only the first is logged per session.
            if (Interlocked.Increment(ref _teeDropCount) == 1)
            {
                _log.Warn("FFmpeg: RTMP 전송이 지연되어 패킷을 버리기 시작했습니다(FIFO queue full) — 네트워크 혼잡 신호.");
            }
            return;
        }
        if (FfmpegProgressParser.IsRtmpSlaveFailureLine(e.Data))
        {
            // onfail=ignore ate the slave: the process keeps encoding to the mp4 while the
            // broadcast is silently dead. Latch the sticky flag; the watchdog rebuilds (§5.4).
            if (Interlocked.Exchange(ref _rtmpLegDown, 1) == 0)
            {
                _log.Warn($"RTMP 송출 경로 실패 감지 — 워치독이 파이프라인을 재구성합니다: {e.Data}");
            }
            return;
        }
        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"FFmpeg: {e.Data}");
        }
    }

    /// <summary>
    /// ffmpeg child-process CPU% across all cores since the previous metrics tick. The
    /// orchestrator's CpuPercent covers only the WPF host process — the encode cost lives here.
    /// Returns -1 when unavailable (first tick / process gone).
    /// </summary>
    private double SampleEncoderCpuPercent()
    {
        try
        {
            var process = _process;
            if (process is null || process.HasExited)
            {
                return -1;
            }
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            double percent = -1;
            if (_lastEncoderCpuSampleUtc != default)
            {
                var wall = (now - _lastEncoderCpuSampleUtc).TotalMilliseconds * Environment.ProcessorCount;
                if (wall > 0)
                {
                    percent = Math.Clamp(
                        (cpu - _lastEncoderCpuTime).TotalMilliseconds / wall * 100, 0, 100);
                }
            }
            _lastEncoderCpuTime = cpu;
            _lastEncoderCpuSampleUtc = now;
            return percent;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return -1; // process raced away between the checks — a metrics tick is best-effort
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
