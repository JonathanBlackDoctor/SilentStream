using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SilentStream.Media.Windows;

/// <summary>
/// Builds the fixed 20 ms PCM chunks emitted by <see cref="WasapiAudioMixer"/>.
/// Keeping the conversion here makes the silence contract independently testable:
/// a missing or silent WASAPI provider still yields a full 48 kHz stereo PCM chunk.
/// </summary>
internal static class ClockedPcmChunker
{
    internal const int SampleRate = 48_000;
    internal const int Channels = 2;
    internal const int FramesPerChunk = SampleRate / 50;
    internal const int SamplesPerChunk = FramesPerChunk * Channels;
    internal const int PcmBytesPerChunk = SamplesPerChunk * sizeof(short);

    /// <summary>
    /// Reads one 20 ms chunk and converts it to signed 16-bit PCM. The complete input buffer is
    /// cleared before each read so a source returning no samples emits actual zero PCM rather than
    /// a previous chunk. The returned value is always a complete chunk length.
    /// </summary>
    internal static int FillPcm16Stereo(ISampleProvider? mixer, float[] floatBuffer, byte[] pcmBuffer)
    {
        ArgumentNullException.ThrowIfNull(floatBuffer);
        ArgumentNullException.ThrowIfNull(pcmBuffer);
        if (floatBuffer.Length < SamplesPerChunk)
        {
            throw new ArgumentException($"At least {SamplesPerChunk} float samples are required.", nameof(floatBuffer));
        }
        if (pcmBuffer.Length < PcmBytesPerChunk)
        {
            throw new ArgumentException($"At least {PcmBytesPerChunk} PCM bytes are required.", nameof(pcmBuffer));
        }

        Array.Clear(floatBuffer, 0, SamplesPerChunk);
        mixer?.Read(floatBuffer, 0, SamplesPerChunk);

        for (var i = 0; i < SamplesPerChunk; i++)
        {
            var sample = Math.Clamp(floatBuffer[i], -1f, 1f);
            var value = (short)(sample * short.MaxValue);
            pcmBuffer[i * sizeof(short)] = (byte)value;
            pcmBuffer[i * sizeof(short) + 1] = (byte)(value >> 8);
        }

        return PcmBytesPerChunk;
    }
}
