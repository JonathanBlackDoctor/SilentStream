using System.Buffers.Binary;
using System.Text.Json;
using NAudio.Wave;
using SilentStream.Inspection.SessionAgent;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class AudioPhysicalProbeTests
{
    [Fact]
    public void Payload_requires_a_concrete_wasapi_endpoint_and_never_accepts_default_or_device_zero()
    {
        Assert.Throws<InvalidDataException>(() => AudioPhysicalProbe.ParseOptions(null));
        Assert.Throws<InvalidDataException>(() => AudioPhysicalProbe.ParseOptions(
            JsonSerializer.SerializeToElement(new { captureEndpointId = "default" })));
        Assert.Throws<InvalidDataException>(() => AudioPhysicalProbe.ParseOptions(
            JsonSerializer.SerializeToElement(new { captureEndpointId = "0" })));

        var parsed = AudioPhysicalProbe.ParseOptions(JsonSerializer.SerializeToElement(new
        {
            captureEndpointId = "{0.0.1.00000000}.{7851e662-f42f-4992-9708-16d62f6f1b39}",
            timeoutSeconds = 12
        }));

        Assert.Equal("{0.0.1.00000000}.{7851e662-f42f-4992-9708-16d62f6f1b39}", parsed.CaptureEndpointId);
        Assert.Equal(12, parsed.TimeoutSeconds);
    }

    [Fact]
    public void Pcm16_native_capture_detects_a_one_kilohertz_tone()
    {
        const int sampleRate = 48000;
        var format = new WaveFormat(sampleRate, 16, 1);
        var bytes = Pcm16Tone(sampleRate, seconds: 1, amplitude: 0.15);

        var metrics = AudioPhysicalProbe.Analyze(bytes, format);
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: false);

        Assert.Null(metrics.AnalysisError);
        Assert.Equal(sampleRate, metrics.SampleRate);
        Assert.Equal(16, metrics.BitsPerSample);
        Assert.Equal(nameof(WaveFormatEncoding.Pcm), metrics.Encoding);
        Assert.InRange(metrics.Rms, 0.105, 0.107);
        Assert.InRange(metrics.ToneCorrelation, 0.70, 0.72);
        Assert.True(verdict.Passed);
        Assert.Equal("healthy", verdict.Reason);
    }

    [Fact]
    public void Float32_stereo_native_capture_checks_channels_independently()
    {
        const int sampleRate = 44100;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        var bytes = FloatStereoToneOnRight(sampleRate, seconds: 1, amplitude: 0.2f);

        var metrics = AudioPhysicalProbe.Analyze(bytes, format);
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: false);

        Assert.Null(metrics.AnalysisError);
        Assert.Equal(2, metrics.Channels);
        Assert.Equal(1, metrics.BestChannel);
        Assert.Equal(32, metrics.BitsPerSample);
        Assert.Equal(nameof(WaveFormatEncoding.IeeeFloat), metrics.Encoding);
        Assert.InRange(metrics.DetectionChannelRms, 0.141, 0.142);
        Assert.InRange(metrics.ToneCorrelation, 0.70, 0.72);
        Assert.True(verdict.Passed);
    }

    [Fact]
    public void Pcm24_native_capture_is_decoded_without_assuming_sixteen_bit_samples()
    {
        const int sampleRate = 32000;
        var format = new WaveFormat(sampleRate, 24, 1);
        var bytes = Pcm24Tone(sampleRate, seconds: 1, amplitude: 0.1);

        var metrics = AudioPhysicalProbe.Analyze(bytes, format);
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: false);

        Assert.Null(metrics.AnalysisError);
        Assert.Equal(24, metrics.BitsPerSample);
        Assert.Equal(bytes.Length, metrics.BytesCaptured);
        Assert.InRange(metrics.Rms, 0.070, 0.072);
        Assert.InRange(metrics.ToneCorrelation, 0.70, 0.72);
        Assert.True(verdict.Passed);
    }

    [Fact]
    public void Silence_preserves_structured_metrics_and_fails_as_low_signal()
    {
        const int sampleRate = 48000;
        var format = new WaveFormat(sampleRate, 16, 1);
        var bytes = new byte[sampleRate * format.BlockAlign];

        var metrics = AudioPhysicalProbe.Analyze(bytes, format);
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: false);

        Assert.Equal(bytes.Length, metrics.BytesCaptured);
        Assert.Equal(sampleRate, metrics.FrameCount);
        Assert.Equal(0, metrics.Rms);
        Assert.Equal(-120, metrics.RmsDb);
        Assert.Equal(0, metrics.ToneCorrelation);
        Assert.False(verdict.Passed);
        Assert.Equal("signal-too-low", verdict.Reason);
    }

    [Fact]
    public void Unsupported_native_encoding_still_preserves_byte_and_format_metrics()
    {
        var format = WaveFormat.CreateMuLawFormat(8000, 1);
        var bytes = Enumerable.Repeat((byte)0xff, 8000).ToArray();

        var metrics = AudioPhysicalProbe.Analyze(bytes, format);
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: false);

        Assert.Equal(bytes.Length, metrics.BytesCaptured);
        Assert.Equal(8000, metrics.SampleRate);
        Assert.Equal(1, metrics.Channels);
        Assert.False(string.IsNullOrWhiteSpace(metrics.AnalysisError));
        Assert.False(verdict.Passed);
        Assert.Equal("analysis-failed", verdict.Reason);
    }

    [Theory]
    [InlineData("Stereo Mix (Realtek Audio)")]
    [InlineData("스테레오 믹스(Realtek(R) Audio)")]
    [InlineData("What U Hear")]
    [InlineData("Monitor of Built-in Audio")]
    public void Loopback_endpoint_can_never_pass_as_a_physical_microphone(string endpointName)
    {
        const int sampleRate = 48000;
        var metrics = AudioPhysicalProbe.Analyze(
            Pcm16Tone(sampleRate, seconds: 1, amplitude: 0.15),
            new WaveFormat(sampleRate, 16, 1));

        Assert.True(AudioPhysicalProbe.IsLoopbackEndpointName(endpointName));
        var verdict = AudioPhysicalProbe.Evaluate(metrics, isLoopback: true);
        Assert.False(verdict.Passed);
        Assert.Equal("loopback-endpoint", verdict.Reason);
    }

    [Fact]
    public void Physical_xaudio_name_is_not_misclassified_as_loopback()
    {
        Assert.False(AudioPhysicalProbe.IsLoopbackEndpointName("Microphone (xAudio)"));
    }

    private static byte[] Pcm16Tone(int sampleRate, int seconds, double amplitude)
    {
        var samples = sampleRate * seconds;
        var bytes = new byte[samples * sizeof(short)];
        for (var index = 0; index < samples; index++)
        {
            var value = (short)Math.Round(short.MaxValue * amplitude
                * Math.Sin(2 * Math.PI * AudioPhysicalProbe.ToneFrequencyHz * index / sampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        return bytes;
    }

    private static byte[] FloatStereoToneOnRight(int sampleRate, int seconds, float amplitude)
    {
        var frames = sampleRate * seconds;
        var bytes = new byte[frames * sizeof(float) * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = amplitude * (float)Math.Sin(
                2 * Math.PI * AudioPhysicalProbe.ToneFrequencyHz * frame / sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((frame * 2 + 1) * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(value));
        }
        return bytes;
    }

    private static byte[] Pcm24Tone(int sampleRate, int seconds, double amplitude)
    {
        var samples = sampleRate * seconds;
        var bytes = new byte[samples * 3];
        for (var index = 0; index < samples; index++)
        {
            var value = (int)Math.Round(8388607 * amplitude
                * Math.Sin(2 * Math.PI * AudioPhysicalProbe.ToneFrequencyHz * index / sampleRate));
            var offset = index * 3;
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
        }
        return bytes;
    }
}
