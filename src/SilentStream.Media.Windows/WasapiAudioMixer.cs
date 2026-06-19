using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Media.Windows;

/// <summary>
/// Captures system audio (WASAPI loopback) plus any number of microphones and mixes them to
/// 48 kHz stereo 16-bit PCM (plan §3.4). Each source has independent gain (>1.0 amplifies),
/// mute and an optional noise gate; the mixer publishes realtime peak/RMS levels for the UI/
/// phone meters and warns when a present microphone falls silent. A missing/failed source never
/// stops the mix — its slot feeds silence and is retried — and device changes rebuild only the
/// affected source. The output pump is wall-clock paced so a silent loopback (which WASAPI does
/// not packetise) cannot make it overproduce and desync A/V.
/// </summary>
public sealed class WasapiAudioMixer : IAudioMixer
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int FramesPerChunk = OutputSampleRate / 50;          // 20 ms
    private const int SamplesPerChunk = FramesPerChunk * OutputChannels;

    private const int LevelEmitIntervalMs = 60;                       // ~16 Hz meter updates
    private const double SilenceThresholdDb = -60;                    // below this a mic reads "no signal"
    private static readonly TimeSpan SilenceGrace = TimeSpan.FromSeconds(10);

    private readonly ILogService _log;
    private readonly object _gate = new();

    private List<SourceChain> _chains = new();
    // Read by the pump thread without the lock; volatile gives it a happens-before view of the
    // graph published under _gate (mirrors SwappableSampleProvider._inner).
    private volatile ISampleProvider? _mixer;
    private readonly LevelAccumulator _masterLevel = new();
    private Task? _pumpTask;
    private CancellationTokenSource? _cts;
    private Timer? _levelsTimer;
    private bool _running;

    private volatile AudioLevels _currentLevels = AudioLevels.Empty;

    public WasapiAudioMixer(ILogService log) => _log = log;

    public event EventHandler<AudioLevels>? LevelsUpdated;
    public event EventHandler<MicSignalStatus>? MicSignalChanged;
    public event EventHandler<AudioBuffer>? SamplesAvailable;

    public AudioLevels CurrentLevels => _currentLevels;

    public IReadOnlyList<AudioSourceSettings> Sources
    {
        get
        {
            lock (_gate)
            {
                return _chains.Select(c => c.Settings).ToList();
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    // ---- source configuration ----

    public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources)
    {
        var incoming = Normalize(sources);
        lock (_gate)
        {
            if (!_running)
            {
                // Not capturing yet: just remember the layout; StartAsync builds the captures.
                ReleaseCaptures();
                _chains = incoming.Select(BuildChain).ToList();
                return;
            }

            if (IsStructuralChange(incoming))
            {
                // Source set / devices changed: rebuild every capture (brief glitch — rare, setup-time).
                ReleaseCaptures();
                _chains = incoming.Select(BuildChain).ToList();
                foreach (var chain in _chains)
                {
                    StartCapture(chain);
                }
                BuildMixer();
                _log.Info($"오디오 소스 재구성: {_chains.Count}개 (시스템 + 마이크 {_chains.Count(c => c.Settings.Kind == AudioSourceKind.Microphone)})");
            }
            else
            {
                // Same sources, only gain/mute/gate changed: update in place (glitch-free).
                foreach (var s in incoming)
                {
                    var chain = _chains.FirstOrDefault(c => c.Settings.Id == s.Id);
                    if (chain is null)
                    {
                        continue;
                    }
                    chain.Settings = s;
                    chain.Gate.Enabled = s.GateEnabled;
                    chain.Gate.ThresholdDb = s.GateThresholdDb;
                    chain.ApplyVolume();
                }
            }
        }
    }

    public void SetGain(string sourceId, double gain)
    {
        lock (_gate)
        {
            var chain = _chains.FirstOrDefault(c => c.Settings.Id == sourceId);
            if (chain is null)
            {
                return;
            }
            chain.Settings = chain.Settings with { Gain = gain };
            chain.ApplyVolume();
        }
    }

    public void SetMuted(string sourceId, bool muted)
    {
        lock (_gate)
        {
            var chain = _chains.FirstOrDefault(c => c.Settings.Id == sourceId);
            if (chain is null)
            {
                return;
            }
            chain.Settings = chain.Settings with { Muted = muted };
            chain.ApplyVolume();
        }
    }

    // ---- lifecycle ----

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }
            if (_chains.Count == 0)
            {
                _chains = Normalize(Array.Empty<AudioSourceSettings>()).Select(BuildChain).ToList();
            }

            foreach (var chain in _chains)
            {
                StartCapture(chain);
            }
            BuildMixer();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running = true;
            _pumpTask = Task.Factory.StartNew(
                () => PumpLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _levelsTimer = new Timer(OnLevelsTick, null, LevelEmitIntervalMs, LevelEmitIntervalMs);
        }
        _log.Info($"오디오 믹서 시작 (소스 {_chains.Count}개)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? pump;
        lock (_gate)
        {
            _running = false;
            _cts?.Cancel();
            pump = _pumpTask;
            _pumpTask = null;
            _levelsTimer?.Dispose();
            _levelsTimer = null;
        }
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        lock (_gate)
        {
            ReleaseCaptures();
            _mixer = null;
        }
        _currentLevels = AudioLevels.Empty;
        _log.Info("오디오 믹서 중지");
    }

    // ---- capture graph ----

    private SourceChain BuildChain(AudioSourceSettings settings)
    {
        var slot = new SwappableSampleProvider(MixFormat);
        var gate = new NoiseGateSampleProvider(slot)
        {
            Enabled = settings.GateEnabled,
            ThresholdDb = settings.GateThresholdDb
        };
        var volume = new VolumeSampleProvider(gate);
        var chain = new SourceChain(settings, slot, gate, volume);
        chain.ApplyVolume();
        return chain;
    }

    private void StartCapture(SourceChain chain)
    {
        try
        {
            IWaveIn capture;
            if (chain.Settings.Kind == AudioSourceKind.System)
            {
                capture = new WasapiLoopbackCapture();
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = string.IsNullOrEmpty(chain.Settings.DeviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : enumerator.GetDevice(chain.Settings.DeviceId);
                capture = new WasapiCapture(device);
            }

            var format = capture.WaveFormat;
            var buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            capture.DataAvailable += (_, e) =>
            {
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                chain.Level.Accumulate(e.Buffer, e.BytesRecorded, format);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    var label = chain.Settings.Kind == AudioSourceKind.System ? "시스템 오디오" : "마이크";
                    _log.Warn($"{label} 캡처 중단({e.Exception.Message}) — 재초기화");
                    TryRestartSource(chain);
                }
            };

            capture.StartRecording();

            chain.Capture = capture;
            chain.CaptureFormat = format;
            chain.Slot.Set(Resampled(buffer));
            chain.Started = true;
            chain.LastSignalUtc = DateTime.UtcNow;
            chain.SignalPresent = true;
        }
        catch (Exception ex)
        {
            // A missing/failed source is supported: it feeds silence and is left out of alarms.
            _log.Warn($"오디오 소스 초기화 실패({chain.Settings.Name}) — 무음으로 계속: {ex.Message}");
            chain.Slot.Set(null);
            chain.Started = false;
        }
    }

    private void TryRestartSource(SourceChain chain)
    {
        Task.Run(() =>
        {
            Thread.Sleep(1000);
            lock (_gate)
            {
                if (!_running || !_chains.Contains(chain))
                {
                    return;
                }
                try
                {
                    chain.ReleaseCapture();
                    StartCapture(chain);
                }
                catch (Exception ex)
                {
                    _log.Error($"오디오 소스 재초기화 실패({chain.Settings.Name})", ex);
                }
            }
        });
    }

    private void BuildMixer()
    {
        // The Volume nodes are stable across capture restarts (slots feed them), so the mixer is
        // only rebuilt when the source set changes.
        _mixer = _chains.Count == 0
            ? null
            : new MixingSampleProvider(_chains.Select(c => (ISampleProvider)c.Volume)) { ReadFully = true };
    }

    private static ISampleProvider Resampled(BufferedWaveProvider source)
    {
        var samples = source.ToSampleProvider();
        if (source.WaveFormat.SampleRate != OutputSampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, OutputSampleRate);
        }
        return samples.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(samples),
            2 => samples,
            _ => new FirstTwoChannelsSampleProvider(samples) // 5.1 등은 앞 2채널만 사용
        };
    }

    private void ReleaseCaptures()
    {
        foreach (var chain in _chains)
        {
            chain.ReleaseCapture();
        }
    }

    // ---- output pump (wall-clock paced) ----

    private void PumpLoop(CancellationToken ct)
    {
        var floatBuffer = new float[SamplesPerChunk];
        var pcmBuffer = new byte[SamplesPerChunk * 2];
        var clock = Stopwatch.StartNew();
        long emittedFrames = 0;

        while (!ct.IsCancellationRequested)
        {
            // Emit exactly enough 20 ms chunks to track wall-clock time — never faster.
            var targetFrames = (long)(clock.Elapsed.TotalSeconds * OutputSampleRate);
            if (targetFrames - emittedFrames < FramesPerChunk)
            {
                if (ct.WaitHandle.WaitOne(5))
                {
                    break;
                }
                continue;
            }

            var mixer = _mixer;
            if (mixer is null)
            {
                emittedFrames += FramesPerChunk; // keep the clock advancing while idle
                continue;
            }

            var read = mixer.Read(floatBuffer, 0, SamplesPerChunk);
            for (var i = 0; i < read; i++)
            {
                var sample = Math.Clamp(floatBuffer[i], -1f, 1f);
                var value = (short)(sample * short.MaxValue);
                pcmBuffer[i * 2] = (byte)value;
                pcmBuffer[i * 2 + 1] = (byte)(value >> 8);
            }
            _masterLevel.AccumulateFloat(floatBuffer, read);
            emittedFrames += FramesPerChunk;

            if (read > 0)
            {
                SamplesAvailable?.Invoke(this, new AudioBuffer(
                    OutputSampleRate, OutputChannels, DateTime.UtcNow,
                    new ReadOnlyMemory<byte>(pcmBuffer, 0, read * 2)));
            }
        }
    }

    // ---- level snapshot + silence detection ----

    private void OnLevelsTick(object? state)
    {
        List<SourceChain> chains;
        lock (_gate)
        {
            chains = _chains.ToList();
        }

        var now = DateTime.UtcNow;
        var sourceLevels = new List<AudioSourceLevel>(chains.Count);
        foreach (var chain in chains)
        {
            var (peakDb, rmsDb) = chain.Level.SnapshotReset();
            sourceLevels.Add(new AudioSourceLevel(chain.Settings.Id, peakDb, rmsDb));
            DetectSilence(chain, rmsDb, now);
        }

        var (masterPeak, masterRms) = _masterLevel.SnapshotReset();
        var levels = new AudioLevels(sourceLevels, masterPeak, masterRms, now);
        _currentLevels = levels;
        LevelsUpdated?.Invoke(this, levels);
    }

    private void DetectSilence(SourceChain chain, double rmsDb, DateTime now)
    {
        if (chain.Settings.Kind != AudioSourceKind.Microphone || !chain.Started)
        {
            return;
        }

        if (rmsDb > SilenceThresholdDb)
        {
            chain.LastSignalUtc = now;
            if (!chain.SignalPresent)
            {
                chain.SignalPresent = true;
                MicSignalChanged?.Invoke(this, new MicSignalStatus(chain.Settings.Id, true, now));
                _log.Info($"마이크 신호 정상 복구: {chain.Settings.Name}");
            }
        }
        else if (chain.SignalPresent && now - chain.LastSignalUtc > SilenceGrace)
        {
            chain.SignalPresent = false;
            MicSignalChanged?.Invoke(this, new MicSignalStatus(chain.Settings.Id, false, now));
            _log.Warn($"마이크 신호 없음(무음 {SilenceGrace.TotalSeconds:F0}초 이상): {chain.Settings.Name} — 연결/음소거 확인 필요");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            _cts?.Cancel();
            _levelsTimer?.Dispose();
            _levelsTimer = null;
            ReleaseCaptures();
            _mixer = null;
        }
        _cts?.Dispose();
    }

    // ---- helpers ----

    /// <summary>Guarantees exactly one system source (first wins) and dedups source ids.</summary>
    private static List<AudioSourceSettings> Normalize(IReadOnlyList<AudioSourceSettings> sources)
    {
        var result = new List<AudioSourceSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasSystem = false;
        foreach (var s in sources)
        {
            if (!seen.Add(s.Id))
            {
                continue;
            }
            if (s.Kind == AudioSourceKind.System)
            {
                if (hasSystem)
                {
                    continue;
                }
                hasSystem = true;
            }
            result.Add(s);
        }
        if (!hasSystem)
        {
            result.Insert(0, new AudioSourceSettings("system", AudioSourceKind.System, null, "시스템 소리"));
        }
        if (result.All(s => s.Kind != AudioSourceKind.Microphone))
        {
            // Default layout includes the default microphone (matches pre-multi-source behaviour).
            result.Add(new AudioSourceSettings("mic:default", AudioSourceKind.Microphone, null, "마이크"));
        }
        return result;
    }

    private bool IsStructuralChange(IReadOnlyList<AudioSourceSettings> incoming)
    {
        if (incoming.Count != _chains.Count)
        {
            return true;
        }
        for (var i = 0; i < incoming.Count; i++)
        {
            var a = incoming[i];
            var b = _chains[i].Settings;
            if (a.Id != b.Id || a.Kind != b.Kind || a.DeviceId != b.DeviceId)
            {
                return true;
            }
        }
        return false;
    }

    // ---- inner types ----

    /// <summary>
    /// Per-source pipeline node: a swappable input slot (so capture restarts don't rebuild the
    /// mixer) → noise gate → volume (the mixer input). Plus a level accumulator and silence state.
    /// </summary>
    private sealed class SourceChain(
        AudioSourceSettings settings,
        SwappableSampleProvider slot,
        NoiseGateSampleProvider gate,
        VolumeSampleProvider volume)
    {
        public AudioSourceSettings Settings { get; set; } = settings;
        public SwappableSampleProvider Slot { get; } = slot;
        public NoiseGateSampleProvider Gate { get; } = gate;
        public VolumeSampleProvider Volume { get; } = volume;
        public LevelAccumulator Level { get; } = new();

        public IWaveIn? Capture { get; set; }
        public WaveFormat? CaptureFormat { get; set; }
        public bool Started { get; set; }
        public bool SignalPresent { get; set; } = true;
        public DateTime LastSignalUtc { get; set; }

        public void ApplyVolume() => Volume.Volume = Settings.Muted ? 0f : (float)Math.Max(0, Settings.Gain);

        public void ReleaseCapture()
        {
            try
            {
                Capture?.Dispose();
            }
            catch
            {
                // best-effort
            }
            Capture = null;
            CaptureFormat = null;
            Slot.Set(null);
            Started = false;
        }
    }

    /// <summary>
    /// A fixed-format (48 kHz float stereo) mixer input whose inner provider can be hot-swapped
    /// when a capture restarts, without rebuilding the mixer. Always returns the requested count,
    /// padding with silence, so the wall-clock pump and meters see a steady stream.
    /// </summary>
    private sealed class SwappableSampleProvider(WaveFormat format) : ISampleProvider
    {
        private volatile ISampleProvider? _inner;

        public WaveFormat WaveFormat { get; } = format;

        public void Set(ISampleProvider? inner) => _inner = inner;

        public int Read(float[] buffer, int offset, int count)
        {
            var inner = _inner;
            var read = inner?.Read(buffer, offset, count) ?? 0;
            if (read < count)
            {
                Array.Clear(buffer, offset + read, count - read);
            }
            return count;
        }
    }

    /// <summary>Thread-safe peak/RMS accumulator. Capture threads add; the level timer snapshots+resets.</summary>
    private sealed class LevelAccumulator
    {
        private readonly object _lock = new();
        private float _peak;
        private double _sumSq;
        private long _count;

        /// <summary>Accumulates from a raw WASAPI capture buffer (any common format).</summary>
        public void Accumulate(byte[] data, int bytes, WaveFormat format)
        {
            var bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample == 0 || bytes < bytesPerSample)
            {
                return;
            }
            var span = data.AsSpan(0, bytes);
            var isFloat = format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat ||
                          (format.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

            float peak = 0;
            double sumSq = 0;
            long count = 0;
            for (var i = 0; i + bytesPerSample <= span.Length; i += bytesPerSample)
            {
                float sample;
                if (isFloat && bytesPerSample == 4)
                {
                    sample = BitConverter.ToSingle(span.Slice(i, 4));
                }
                else if (bytesPerSample == 2)
                {
                    sample = BitConverter.ToInt16(span.Slice(i, 2)) / 32768f;
                }
                else if (bytesPerSample == 3)
                {
                    var v = span[i] | (span[i + 1] << 8) | (sbyte)span[i + 2] << 16;
                    sample = v / 8388608f;
                }
                else if (bytesPerSample == 4)
                {
                    sample = BitConverter.ToInt32(span.Slice(i, 4)) / 2147483648f;
                }
                else
                {
                    return; // unsupported width — skip metering, audio path still works
                }

                var a = Math.Abs(sample);
                if (a > peak)
                {
                    peak = a;
                }
                sumSq += (double)sample * sample;
                count++;
            }
            Add(peak, sumSq, count);
        }

        /// <summary>Accumulates from the already-float mixed master buffer.</summary>
        public void AccumulateFloat(float[] buffer, int count)
        {
            float peak = 0;
            double sumSq = 0;
            for (var i = 0; i < count; i++)
            {
                var a = Math.Abs(buffer[i]);
                if (a > peak)
                {
                    peak = a;
                }
                sumSq += (double)buffer[i] * buffer[i];
            }
            Add(peak, sumSq, count);
        }

        private void Add(float peak, double sumSq, long count)
        {
            lock (_lock)
            {
                if (peak > _peak)
                {
                    _peak = peak;
                }
                _sumSq += sumSq;
                _count += count;
            }
        }

        public (double PeakDb, double RmsDb) SnapshotReset()
        {
            float peak;
            double rms;
            lock (_lock)
            {
                peak = _peak;
                rms = _count > 0 ? Math.Sqrt(_sumSq / _count) : 0;
                _peak = 0;
                _sumSq = 0;
                _count = 0;
            }
            return (ToDb(peak), ToDb(rms));
        }

        private static double ToDb(double linear) =>
            linear <= 1e-7
                ? AudioLevels.SilenceFloorDb
                : Math.Max(AudioLevels.SilenceFloorDb, 20 * Math.Log10(linear));
    }

    /// <summary>Downmixes >2-channel sources by keeping the front L/R channels.</summary>
    private sealed class FirstTwoChannelsSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var frames = count / 2;
            var temp = new float[frames * channels];
            var readSamples = source.Read(temp, 0, temp.Length);
            var readFrames = readSamples / channels;
            for (var f = 0; f < readFrames; f++)
            {
                buffer[offset + f * 2] = temp[f * channels];
                buffer[offset + f * 2 + 1] = temp[f * channels + 1];
            }
            return readFrames * 2;
        }
    }
}
