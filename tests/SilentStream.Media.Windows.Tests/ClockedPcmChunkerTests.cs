using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SilentStream.Media.Windows;
using Xunit;

namespace SilentStream.Media.Windows.Tests;

public sealed class ClockedPcmChunkerTests
{
    [Fact]
    public void SilentMixer_AlwaysProducesFullTwentyMillisecondZeroPcmChunk()
    {
        var floats = Enumerable.Repeat(0.75f, ClockedPcmChunker.SamplesPerChunk).ToArray();
        var pcm = Enumerable.Repeat((byte)0xFF, ClockedPcmChunker.PcmBytesPerChunk).ToArray();

        var length = ClockedPcmChunker.FillPcm16Stereo(new SilentSampleProvider(), floats, pcm);

        Assert.Equal(ClockedPcmChunker.PcmBytesPerChunk, length);
        Assert.All(floats, sample => Assert.Equal(0f, sample));
        Assert.All(pcm, sample => Assert.Equal((byte)0, sample));
    }

    private sealed class SilentSampleProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(
            ClockedPcmChunker.SampleRate, ClockedPcmChunker.Channels);

        public int Read(float[] buffer, int offset, int count) => 0;
    }
}
