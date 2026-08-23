using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SilentStream.Inspection.SessionAgent;

/// <summary>Plays a quiet local 1 kHz chirp and returns correlation metrics only; no audio leaves the PC.</summary>
public static class AudioPhysicalProbe
{
    public static async Task<object> RunAsync()
    {
        const int sampleRate = 44100;
        var bytes = new MemoryStream();
        var gate = new object();
        using var capture = new WaveInEvent { DeviceNumber = 0, WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 50 };
        using var playback = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 100 };
        var recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, e) => { lock (gate) bytes.Write(e.Buffer, 0, e.BytesRecorded); };
        capture.RecordingStopped += (_, e) => { if (e.Exception is null) recordingStopped.TrySetResult(); else recordingStopped.TrySetException(e.Exception); };
        playback.PlaybackStopped += (_, e) => { if (e.Exception is null) playbackStopped.TrySetResult(); else playbackStopped.TrySetException(e.Exception); };
        var chirp = new SignalGenerator(sampleRate, 1) { Type = SignalGeneratorType.Sin, Frequency = 1000, Gain = 0.12 }
            .Take(TimeSpan.FromSeconds(1));
        playback.Init(chirp);
        capture.StartRecording();
        await Task.Delay(500).ConfigureAwait(false);
        playback.Play();
        await playbackStopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        capture.StopRecording();
        await recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        byte[] captured; lock (gate) captured = bytes.ToArray();
        if (captured.Length < sampleRate) throw new InvalidOperationException("Default microphone returned too little audio.");
        var count = captured.Length / 2;
        double square = 0, sin = 0, cos = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToInt16(captured, i * 2) / 32768d;
            var angle = 2 * Math.PI * 1000 * i / sampleRate;
            square += sample * sample; sin += sample * Math.Sin(angle); cos += sample * Math.Cos(angle);
        }
        var rms = Math.Sqrt(square / count);
        var correlation = rms <= 1e-9 ? 0 : Math.Sqrt(sin * sin + cos * cos) / (count * rms);
        var passed = rms >= 0.001 && correlation >= 0.03;
        return new { passed, sampleRate, sampleCount = count, rms, toneCorrelation = correlation, speakerDevice = "default", microphoneDevice = "default" };
    }
}
