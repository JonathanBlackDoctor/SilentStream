using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Media.Windows;

/// <summary>
/// Mixes system audio (WASAPI loopback) with a selectable microphone into 48kHz stereo
/// 16-bit PCM with independent volumes (plan §3.4). A missing/failed microphone never
/// stops the mix — system audio continues alone; device changes trigger re-init.
/// </summary>
public sealed class WasapiAudioMixer : IAudioMixer
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;

    private readonly ILogService _log;
    private readonly object _gate = new();

    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _systemBuffer;
    private BufferedWaveProvider? _micBuffer;
    private VolumeSampleProvider? _systemVolumeProvider;
    private VolumeSampleProvider? _micVolumeProvider;
    private ISampleProvider? _mixer;
    private Task? _pumpTask;
    private CancellationTokenSource? _cts;

    private double _systemVolume = 1.0;
    private double _micVolume = 1.0;
    private string? _micDeviceId;

    public WasapiAudioMixer(ILogService log) => _log = log;

    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            _systemVolume = Math.Clamp(value, 0, 1);
            if (_systemVolumeProvider is not null)
            {
                _systemVolumeProvider.Volume = (float)_systemVolume;
            }
        }
    }

    public double MicVolume
    {
        get => _micVolume;
        set
        {
            _micVolume = Math.Clamp(value, 0, 1);
            if (_micVolumeProvider is not null)
            {
                _micVolumeProvider.Volume = (float)_micVolume;
            }
        }
    }

    public string? MicDeviceId
    {
        get => _micDeviceId;
        set
        {
            if (_micDeviceId == value)
            {
                return;
            }
            _micDeviceId = value;
            if (_pumpTask is not null)
            {
                RestartMicCapture();
            }
        }
    }

    public event EventHandler<AudioBuffer>? SamplesAvailable;

    public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pumpTask is not null)
            {
                return Task.CompletedTask;
            }

            StartSystemCapture();
            StartMicCapture();
            BuildMixer();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pumpTask = Task.Factory.StartNew(
                () => PumpLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        _log.Info("오디오 믹서 시작 (시스템 + 마이크)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? pump;
        lock (_gate)
        {
            _cts?.Cancel();
            pump = _pumpTask;
            _pumpTask = null;
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
        ReleaseCaptures();
        _log.Info("오디오 믹서 중지");
    }

    private void StartSystemCapture()
    {
        _systemCapture = new WasapiLoopbackCapture();
        _systemBuffer = new BufferedWaveProvider(_systemCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        _systemCapture.DataAvailable += (_, e) => _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _systemCapture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                // Device unplugged or format change: rebuild loopback capture (plan §3.4).
                _log.Warn($"시스템 오디오 캡처 중단({e.Exception.Message}) — 재초기화");
                TryRestart(StartSystemCapture);
            }
        };
        _systemCapture.StartRecording();
    }

    private void StartMicCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = _micDeviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(_micDeviceId);

            _micCapture = new WasapiCapture(device);
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            _micCapture.DataAvailable += (_, e) => _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _micCapture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    _log.Warn($"마이크 캡처 중단({e.Exception.Message}) — 시스템 오디오만으로 계속");
                    _micBuffer = null;
                    TryRestart(StartMicCapture);
                }
            };
            _micCapture.StartRecording();
        }
        catch (Exception ex)
        {
            // No mic is a supported configuration: keep streaming with system audio only.
            _log.Warn($"마이크 초기화 실패 — 시스템 오디오만 사용합니다: {ex.Message}");
            _micCapture = null;
            _micBuffer = null;
        }
    }

    private void BuildMixer()
    {
        var inputs = new List<ISampleProvider>();

        _systemVolumeProvider = new VolumeSampleProvider(Resampled(_systemBuffer!))
        {
            Volume = (float)_systemVolume
        };
        inputs.Add(_systemVolumeProvider);

        if (_micBuffer is not null)
        {
            _micVolumeProvider = new VolumeSampleProvider(Resampled(_micBuffer))
            {
                Volume = (float)_micVolume
            };
            inputs.Add(_micVolumeProvider);
        }

        _mixer = new MixingSampleProvider(inputs) { ReadFully = true };
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

    /// <summary>Reads mixed float samples, converts to s16le, raises SamplesAvailable at ~50Hz.</summary>
    private void PumpLoop(CancellationToken ct)
    {
        var samplesPerChunk = OutputSampleRate / 50 * OutputChannels; // 20ms
        var floatBuffer = new float[samplesPerChunk];
        var pcmBuffer = new byte[samplesPerChunk * 2];

        while (!ct.IsCancellationRequested)
        {
            var mixer = _mixer;
            if (mixer is null)
            {
                Thread.Sleep(20);
                continue;
            }

            var read = mixer.Read(floatBuffer, 0, floatBuffer.Length);
            if (read == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            for (var i = 0; i < read; i++)
            {
                var sample = Math.Clamp(floatBuffer[i], -1f, 1f);
                var value = (short)(sample * short.MaxValue);
                pcmBuffer[i * 2] = (byte)value;
                pcmBuffer[i * 2 + 1] = (byte)(value >> 8);
            }

            SamplesAvailable?.Invoke(this, new AudioBuffer(
                OutputSampleRate, OutputChannels, DateTime.UtcNow,
                new ReadOnlyMemory<byte>(pcmBuffer, 0, read * 2)));
        }
    }

    private void RestartMicCapture()
    {
        lock (_gate)
        {
            _micCapture?.Dispose();
            _micCapture = null;
            _micBuffer = null;
            StartMicCapture();
            BuildMixer();
        }
        _log.Info("마이크 장치 변경 적용");
    }

    private void TryRestart(Action restart)
    {
        Task.Run(() =>
        {
            Thread.Sleep(1000);
            lock (_gate)
            {
                if (_cts is { IsCancellationRequested: false })
                {
                    try
                    {
                        restart();
                        BuildMixer();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("오디오 캡처 재초기화 실패", ex);
                    }
                }
            }
        });
    }

    private void ReleaseCaptures()
    {
        lock (_gate)
        {
            _systemCapture?.Dispose();
            _systemCapture = null;
            _micCapture?.Dispose();
            _micCapture = null;
            _mixer = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        ReleaseCaptures();
        _cts?.Dispose();
    }
}
