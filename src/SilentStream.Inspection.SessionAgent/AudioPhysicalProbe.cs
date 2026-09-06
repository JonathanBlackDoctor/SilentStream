using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SilentStream.Inspection.SessionAgent;

/// <summary>
/// Opens one explicitly approved WASAPI capture endpoint, plays a short quiet 1 kHz tone burst on
/// the current default render endpoint, and measures whether that tone arrived at the microphone.
/// Captured audio remains in memory and is reduced to metrics only.
/// </summary>
public static class AudioPhysicalProbe
{
    public const double ToneFrequencyHz = 1000;
    public const double MinimumRms = 0.001;
    public const double MinimumToneCorrelation = 0.03;

    private const int ChirpSampleRate = 48000;
    private const int ChirpChannels = 2;
    private const double ChirpGain = 0.08;
    private static readonly TimeSpan ChirpDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan CapturePreRoll = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CaptureTail = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MinimumCapturedDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopEventBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(2);
    private const int MaximumCapturedBytes = 32 * 1024 * 1024;
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    public static AudioPhysicalProbeOptions ParseOptions(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("captureEndpointId", out var endpointValue)
            || endpointValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(endpointValue.GetString()))
        {
            throw new InvalidDataException("Missing payload.captureEndpointId; a concrete WASAPI capture endpoint is required.");
        }

        var endpointId = ValidateEndpointId(endpointValue.GetString());

        var timeoutSeconds = 8;
        if (value.TryGetProperty("timeoutSeconds", out var timeoutValue))
        {
            if (!timeoutValue.TryGetInt32(out timeoutSeconds))
                throw new InvalidDataException("payload.timeoutSeconds must be an integer.");
            timeoutSeconds = Math.Clamp(timeoutSeconds, 4, 30);
        }
        return new AudioPhysicalProbeOptions(endpointId, timeoutSeconds);
    }

    public static async Task<AudioPhysicalProbeResult> RunAsync(
        AudioPhysicalProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var captureEndpointId = ValidateEndpointId(options.CaptureEndpointId);

        var elapsed = Stopwatch.StartNew();
        var capturedBytes = new MemoryStream();
        var captureBytesGate = new object();
        var acceptingCaptureData = true;
        var captureTruncated = false;

        MMDevice? captureDevice = null;
        MMDevice? renderDevice = null;
        WasapiCapture? capture = null;
        WasapiOut? playback = null;
        WaveFormat? captureFormat = null;
        TaskCompletionSource<Exception?>? recordingStopped = null;
        TaskCompletionSource<Exception?>? playbackStopped = null;
        EventHandler<WaveInEventArgs>? dataHandler = null;
        var captureStarted = false;
        var playbackStarted = false;

        string? resolvedCaptureId = null;
        string? resolvedCaptureName = null;
        string? resolvedRenderId = null;
        string? resolvedRenderName = null;
        var isLoopback = false;
        string? operationFailure = null;
        string? operationError = null;
        var externalCancellation = false;
        var gateAcquired = false;
        var normalizedTimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 4, 30);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(normalizedTimeoutSeconds));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await ProbeGate.WaitAsync(operation.Token).ConfigureAwait(false);
            gateAcquired = true;
            operation.Token.ThrowIfCancellationRequested();
            using (var enumerator = new MMDeviceEnumerator())
            {
                // Never use WasapiCapture() or WaveIn device 0 here. GetDevice resolves only the ID
                // supplied by the coordinator after it matched the room's required microphone.
                captureDevice = enumerator.GetDevice(captureEndpointId);
                resolvedCaptureId = captureDevice.ID;
                resolvedCaptureName = captureDevice.FriendlyName;
                isLoopback = IsLoopbackEndpointName(resolvedCaptureName);

                if (captureDevice.DataFlow != DataFlow.Capture)
                    throw new ProbeFailureException("not-a-capture-endpoint", "The requested endpoint is not a capture device.");
                if ((captureDevice.State & DeviceState.Active) == 0)
                    throw new ProbeFailureException("capture-endpoint-inactive", "The requested capture endpoint is not active.");
                if (isLoopback)
                    throw new ProbeFailureException("loopback-endpoint", "Stereo Mix and other loopback endpoints cannot prove a physical microphone path.");

                renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                resolvedRenderId = renderDevice.ID;
                resolvedRenderName = renderDevice.FriendlyName;

                // NAudio captures SynchronizationContext in its constructors. Force callbacks onto
                // the native worker threads so an absent/blocked UI message loop cannot stall the
                // completion events. The handlers below only complete asynchronous TCS instances.
                var previousContext = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    capture = new WasapiCapture(captureDevice, useEventSync: false, audioBufferMillisecondsLength: 100);
                    captureFormat = capture.WaveFormat;
                    playback = new WasapiOut(renderDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }

            recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            playbackStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dataHandler = (_, eventArgs) =>
            {
                lock (captureBytesGate)
                {
                    if (!acceptingCaptureData || eventArgs.BytesRecorded <= 0) return;
                    var remaining = MaximumCapturedBytes - (int)capturedBytes.Length;
                    if (remaining <= 0)
                    {
                        captureTruncated = true;
                        return;
                    }
                    var count = Math.Min(remaining, eventArgs.BytesRecorded);
                    capturedBytes.Write(eventArgs.Buffer, 0, count);
                    if (count < eventArgs.BytesRecorded) captureTruncated = true;
                }
            };
            capture.DataAvailable += dataHandler;
            capture.RecordingStopped += (_, eventArgs) => recordingStopped.TrySetResult(eventArgs.Exception);
            playback.PlaybackStopped += (_, eventArgs) => playbackStopped.TrySetResult(eventArgs.Exception);
            playback.Init(new QuietChirpSampleProvider(
                ChirpSampleRate, ChirpChannels, ToneFrequencyHz, ChirpGain, ChirpDuration).ToWaveProvider());

            capture.StartRecording();
            captureStarted = true;
            await Task.Delay(CapturePreRoll, operation.Token).ConfigureAwait(false);
            ThrowIfCaptureStopped(recordingStopped);

            playback.Play();
            playbackStarted = true;
            var firstStopped = await Task.WhenAny(playbackStopped.Task, recordingStopped.Task)
                .WaitAsync(operation.Token).ConfigureAwait(false);
            if (ReferenceEquals(firstStopped, recordingStopped.Task))
                throw CaptureStoppedFailure(await recordingStopped.Task.ConfigureAwait(false));

            var playbackError = await playbackStopped.Task.ConfigureAwait(false);
            if (playbackError is not null)
                throw new ProbeFailureException("playback-failed", playbackError.Message, playbackError);
            ThrowIfCaptureStopped(recordingStopped);

            await Task.Delay(CaptureTail, operation.Token).ConfigureAwait(false);
            capture.StopRecording();
            var captureError = await recordingStopped.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            if (captureError is not null)
                throw new ProbeFailureException("capture-failed", captureError.Message, captureError);
        }
        catch (ProbeFailureException ex)
        {
            operationFailure = ex.Reason;
            operationError = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationFailure = "cancelled";
            operationError = "The physical audio probe was cancelled.";
            externalCancellation = true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            operationFailure = "timeout";
            operationError = $"The physical audio probe exceeded {normalizedTimeoutSeconds} seconds.";
        }
        catch (Exception ex)
        {
            operationFailure = "probe-failed";
            operationError = ex.Message;
        }

        bool cleanupCompleted;
        try
        {
            cleanupCompleted = await CleanupAsync(
                capture, captureDevice, captureStarted, recordingStopped,
                playback, renderDevice, playbackStarted).ConfigureAwait(false);
        }
        finally
        {
            if (gateAcquired) ProbeGate.Release();
        }

        byte[] captured;
        lock (captureBytesGate)
        {
            acceptingCaptureData = false;
            captured = capturedBytes.ToArray();
        }
        if (capture is not null && dataHandler is not null)
        {
            try { capture.DataAvailable -= dataHandler; } catch { }
        }

        if (externalCancellation)
        {
            elapsed.Stop();
            throw new OperationCanceledException(cancellationToken);
        }

        var metrics = captureFormat is null
            ? AudioSignalMetrics.Empty(captured.LongLength)
            : Analyze(captured, captureFormat, ToneFrequencyHz);
        var verdict = Evaluate(metrics, isLoopback);
        var reason = operationFailure ?? (!cleanupCompleted ? "cleanup-timeout" : verdict.Reason);
        var error = operationError ?? metrics.AnalysisError
            ?? (!cleanupCompleted ? "Audio device cleanup did not complete within two seconds." : null);
        var passed = operationFailure is null && cleanupCompleted && verdict.Passed;

        elapsed.Stop();
        return new AudioPhysicalProbeResult(
            passed,
            reason,
            captureEndpointId,
            resolvedCaptureId,
            resolvedCaptureName,
            isLoopback,
            resolvedRenderId,
            resolvedRenderName,
            metrics.SampleRate,
            metrics.Channels,
            metrics.BitsPerSample,
            metrics.Encoding,
            metrics.BytesCaptured,
            metrics.SampleCount,
            metrics.FrameCount,
            metrics.Rms,
            metrics.RmsDb,
            metrics.DetectionChannelRms,
            metrics.ToneCorrelation,
            metrics.ToneRms,
            metrics.BestChannel,
            ToneFrequencyHz,
            captureTruncated,
            !cleanupCompleted,
            reason == "timeout",
            elapsed.ElapsedMilliseconds,
            error);
    }

    /// <summary>Converts PCM/IEEE-float native capture bytes to channel-aware signal metrics.</summary>
    public static AudioSignalMetrics Analyze(byte[] captured, WaveFormat format, double toneFrequencyHz = ToneFrequencyHz)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(format);
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BlockAlign <= 0)
            return AudioSignalMetrics.Empty(captured.LongLength, "Invalid capture format.", format);

        var alignedBytes = captured.Length - captured.Length % format.BlockAlign;
        if (alignedBytes <= 0)
            return AudioSignalMetrics.Empty(captured.LongLength, null, format);

        try
        {
            using var source = new RawSourceWaveStream(captured, 0, alignedBytes, format);
            var samples = source.ToSampleProvider();
            var channels = samples.WaveFormat.Channels;
            var buffer = new float[4096 * channels];
            var square = new double[channels];
            var sine = new double[channels];
            var cosine = new double[channels];
            long frameCount = 0;

            while (true)
            {
                var read = samples.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var complete = read - read % channels;
                for (var offset = 0; offset < complete; offset += channels)
                {
                    var angle = 2 * Math.PI * toneFrequencyHz * frameCount / samples.WaveFormat.SampleRate;
                    var sin = Math.Sin(angle);
                    var cos = Math.Cos(angle);
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sample = buffer[offset + channel];
                        if (!float.IsFinite(sample)) sample = 0;
                        var value = Math.Clamp((double)sample, -1, 1);
                        square[channel] += value * value;
                        sine[channel] += value * sin;
                        cosine[channel] += value * cos;
                    }
                    frameCount++;
                }
            }

            if (frameCount == 0)
                return AudioSignalMetrics.Empty(captured.LongLength, null, format);

            var channelRms = new double[channels];
            var correlations = new double[channels];
            var toneRms = new double[channels];
            for (var channel = 0; channel < channels; channel++)
            {
                channelRms[channel] = Math.Sqrt(square[channel] / frameCount);
                var magnitude = Math.Sqrt(sine[channel] * sine[channel] + cosine[channel] * cosine[channel]);
                correlations[channel] = channelRms[channel] <= 1e-12
                    ? 0
                    : Math.Clamp(magnitude / (frameCount * channelRms[channel]), 0, 1);
                toneRms[channel] = Math.Sqrt(2) * magnitude / frameCount;
            }

            var eligible = Enumerable.Range(0, channels).Where(channel => channelRms[channel] >= MinimumRms).ToArray();
            var bestChannel = eligible.Length > 0
                ? eligible.MaxBy(channel => correlations[channel])
                : Enumerable.Range(0, channels).MaxBy(channel => channelRms[channel]);
            var rms = channelRms.Max();
            var rmsDb = rms <= 1e-6 ? -120 : Math.Max(-120, 20 * Math.Log10(rms));
            return new AudioSignalMetrics(
                captured.LongLength,
                checked(frameCount * channels),
                frameCount,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                format.Encoding.ToString(),
                rms,
                rmsDb,
                channelRms[bestChannel],
                correlations[bestChannel],
                toneRms[bestChannel],
                bestChannel,
                null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return AudioSignalMetrics.Empty(captured.LongLength, ex.Message, format);
        }
    }

    public static AudioSignalVerdict Evaluate(AudioSignalMetrics metrics, bool isLoopback)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (isLoopback) return new AudioSignalVerdict(false, "loopback-endpoint");
        if (!string.IsNullOrWhiteSpace(metrics.AnalysisError)) return new AudioSignalVerdict(false, "analysis-failed");
        if (metrics.SampleRate <= 0 || metrics.FrameCount < metrics.SampleRate * MinimumCapturedDuration.TotalSeconds)
            return new AudioSignalVerdict(false, metrics.BytesCaptured == 0 ? "no-capture-data" : "insufficient-capture-data");
        if (metrics.DetectionChannelRms < MinimumRms) return new AudioSignalVerdict(false, "signal-too-low");
        if (metrics.ToneCorrelation < MinimumToneCorrelation) return new AudioSignalVerdict(false, "chirp-not-detected");
        return new AudioSignalVerdict(true, "healthy");
    }

    public static bool IsLoopbackEndpointName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string[] markers =
        [
            "stereo mix", "스테레오 믹스", "스테레오믹스", "what u hear", "what you hear",
            "wave out mix", "wave-out mix", "loopback", "rec. playback", "녹음 재생", "monitor of"
        ];
        return markers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string ValidateEndpointId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A concrete WASAPI capture endpoint is required.");
        var endpointId = value.Trim();
        if (endpointId.Length > 2048 || endpointId.IndexOf('\0') >= 0
            || endpointId.Equals("default", StringComparison.OrdinalIgnoreCase)
            || endpointId.Equals("mic:default", StringComparison.OrdinalIgnoreCase)
            || int.TryParse(endpointId, out _))
        {
            throw new InvalidDataException("captureEndpointId must be an actual WASAPI endpoint ID, not a default device alias or index.");
        }
        return endpointId;
    }

    private static void ThrowIfCaptureStopped(TaskCompletionSource<Exception?> recordingStopped)
    {
        if (!recordingStopped.Task.IsCompleted) return;
        throw CaptureStoppedFailure(recordingStopped.Task.GetAwaiter().GetResult());
    }

    private static ProbeFailureException CaptureStoppedFailure(Exception? error) => error is null
        ? new ProbeFailureException("capture-stopped", "The capture endpoint stopped before the probe completed.")
        : new ProbeFailureException("capture-failed", error.Message, error);

    private static async Task<bool> CleanupAsync(
        WasapiCapture? capture,
        MMDevice? captureDevice,
        bool captureStarted,
        TaskCompletionSource<Exception?>? recordingStopped,
        WasapiOut? playback,
        MMDevice? renderDevice,
        bool playbackStarted)
    {
        // Both NAudio Dispose implementations can synchronously join their worker threads. Run
        // both ownership chains away from the request thread so CleanupBudget bounds even a bad
        // driver that blocks inside Dispose rather than only the preceding stop event.
        var captureCleanup = Task.Run(() => CleanupCaptureAsync(capture, captureDevice, captureStarted, recordingStopped));
        var playbackCleanup = Task.Run(() => CleanupPlayback(playback, renderDevice, playbackStarted));
        try
        {
            await Task.WhenAll(captureCleanup, playbackCleanup).WaitAsync(CleanupBudget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // The cleanup tasks retain ownership and continue in the background. Blocking Dispose
            // here would join a faulty driver thread and hang the interactive session agent.
            return false;
        }
    }

    private static async Task CleanupCaptureAsync(
        WasapiCapture? capture,
        MMDevice? device,
        bool started,
        TaskCompletionSource<Exception?>? stopped)
    {
        try
        {
            if (capture is not null)
            {
                if (started && stopped is not null && !stopped.Task.IsCompleted)
                {
                    try { capture.StopRecording(); } catch { }
                    // A broken driver may return from StopRecording without ever raising
                    // RecordingStopped. Do not leave this ownership task waiting forever; after
                    // a short grace period, continue to Dispose on this background thread. The
                    // outer cleanup budget still protects the request if Dispose itself blocks.
                    try { await stopped.Task.WaitAsync(StopEventBudget).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                try { capture.Dispose(); } catch { }
            }
        }
        finally
        {
            try { device?.Dispose(); } catch { }
        }
    }

    private static void CleanupPlayback(WasapiOut? playback, MMDevice? device, bool started)
    {
        try
        {
            if (playback is not null)
            {
                if (started)
                {
                    try { playback.Stop(); } catch { }
                }
                try { playback.Dispose(); } catch { }
            }
        }
        finally
        {
            try { device?.Dispose(); } catch { }
        }
    }

    private sealed class QuietChirpSampleProvider : ISampleProvider
    {
        private readonly long _totalFrames;
        private readonly int _fadeFrames;
        private readonly double _frequency;
        private readonly double _gain;
        private long _position;

        public QuietChirpSampleProvider(int sampleRate, int channels, double frequency, double gain, TimeSpan duration)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _totalFrames = Math.Max(1, (long)Math.Round(duration.TotalSeconds * sampleRate));
            _fadeFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.03));
            _frequency = frequency;
            _gain = gain;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = WaveFormat.Channels;
            var requestedFrames = count / channels;
            var frames = (int)Math.Min(requestedFrames, _totalFrames - _position);
            for (var frame = 0; frame < frames; frame++)
            {
                var absolute = _position + frame;
                var fadeIn = Math.Min(1, absolute / (double)_fadeFrames);
                var fadeOut = Math.Min(1, (_totalFrames - 1 - absolute) / (double)_fadeFrames);
                var envelope = Math.Max(0, Math.Min(fadeIn, fadeOut));
                var sample = (float)(_gain * envelope
                    * Math.Sin(2 * Math.PI * _frequency * absolute / WaveFormat.SampleRate));
                for (var channel = 0; channel < channels; channel++)
                    buffer[offset + frame * channels + channel] = sample;
            }
            _position += frames;
            return frames * channels;
        }
    }

    private sealed class ProbeFailureException(string reason, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        public string Reason { get; } = reason;
    }
}

public sealed record AudioPhysicalProbeOptions(string CaptureEndpointId, int TimeoutSeconds = 8);

public sealed record AudioSignalMetrics(
    long BytesCaptured,
    long SampleCount,
    long FrameCount,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string? Encoding,
    double Rms,
    double RmsDb,
    double DetectionChannelRms,
    double ToneCorrelation,
    double ToneRms,
    int BestChannel,
    string? AnalysisError)
{
    public static AudioSignalMetrics Empty(long bytesCaptured, string? error = null, WaveFormat? format = null) => new(
        bytesCaptured, 0, 0, format?.SampleRate ?? 0, format?.Channels ?? 0, format?.BitsPerSample ?? 0,
        format?.Encoding.ToString(), 0, -120, 0, 0, 0, -1, error);
}

public sealed record AudioSignalVerdict(bool Passed, string Reason);

public sealed record AudioPhysicalProbeResult(
    bool Passed,
    string Reason,
    string CaptureEndpointId,
    string? ResolvedCaptureEndpointId,
    string? ResolvedCaptureEndpointName,
    bool IsLoopback,
    string? SpeakerEndpointId,
    string? SpeakerEndpointName,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string? Encoding,
    long BytesCaptured,
    long SampleCount,
    long FrameCount,
    double Rms,
    double RmsDb,
    double DetectionChannelRms,
    double ToneCorrelation,
    double ToneRms,
    int BestChannel,
    double ToneFrequencyHz,
    bool CaptureTruncated,
    bool CleanupTimedOut,
    bool TimedOut,
    long DurationMilliseconds,
    string? Error);
