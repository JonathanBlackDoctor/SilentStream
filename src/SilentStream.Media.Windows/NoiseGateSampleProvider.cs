using NAudio.Wave;

namespace SilentStream.Media.Windows;

/// <summary>
/// A per-source noise gate (OBS 대비 노이즈 게이트): when the signal sits below an open
/// threshold for longer than the hold time it is smoothly attenuated to silence, killing
/// room hiss / fan noise between speech; once it rises above the threshold it opens quickly.
/// Operates in-place on the 48 kHz float-stereo mixer stream and is realtime-adjustable
/// (<see cref="Enabled"/> / <see cref="ThresholdDb"/>) so the UI can change it without a glitch.
/// </summary>
public sealed class NoiseGateSampleProvider : ISampleProvider
{
    private const double AttackMs = 5;
    private const double ReleaseMs = 150;
    private const double HoldMs = 200;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _attackCoef;
    private readonly float _releaseCoef;
    private readonly int _holdFrames;

    private float _envelope;     // current applied gain envelope, 0..1
    private int _holdCounter;    // frames remaining before release may begin

    public NoiseGateSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        var sr = source.WaveFormat.SampleRate;
        // Per-frame one-pole smoothing coefficients (frames/sec == sample rate).
        _attackCoef = (float)(1.0 - Math.Exp(-1.0 / (AttackMs / 1000.0 * sr)));
        _releaseCoef = (float)(1.0 - Math.Exp(-1.0 / (ReleaseMs / 1000.0 * sr)));
        _holdFrames = (int)(HoldMs / 1000.0 * sr);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>When false the gate passes audio through untouched.</summary>
    public bool Enabled { get; set; }

    /// <summary>Open threshold in dBFS; frames below it (after hold) are attenuated.</summary>
    public double ThresholdDb { get; set; } = -45;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (!Enabled || _channels <= 0)
        {
            return read;
        }

        var threshold = (float)Math.Pow(10, ThresholdDb / 20.0);
        for (var i = 0; i + _channels <= read; i += _channels)
        {
            var framePeak = 0f;
            for (var c = 0; c < _channels; c++)
            {
                var a = Math.Abs(buffer[offset + i + c]);
                if (a > framePeak)
                {
                    framePeak = a;
                }
            }

            float target;
            if (framePeak >= threshold)
            {
                _holdCounter = _holdFrames;
                target = 1f;
            }
            else if (_holdCounter > 0)
            {
                _holdCounter--;
                target = 1f;
            }
            else
            {
                target = 0f;
            }

            var coef = target > _envelope ? _attackCoef : _releaseCoef;
            _envelope += (target - _envelope) * coef;

            for (var c = 0; c < _channels; c++)
            {
                buffer[offset + i + c] *= _envelope;
            }
        }

        return read;
    }
}
