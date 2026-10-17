using System.Collections.Concurrent;
using NAudio.Wave;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Media.Windows;
using Xunit;

namespace SilentStream.Media.Windows.Tests;

public sealed class MicrophoneHotplugTests
{
    [Fact]
    public async Task Hotplug_and_new_endpoint_id_are_mixed_without_restarting_healthy_inputs()
    {
        var clock = new ManualClock();
        AudioDeviceInfo[] devices = [new("a", "A", false)];
        var opened = new ConcurrentDictionary<string, FakeCapture>();
        using var mixer = new WasapiAudioMixer(new NullLog(), () => devices, s =>
        {
            var capture = new FakeCapture();
            opened[s.Id] = capture;
            return capture;
        }, clock, enableTimers: false);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            var system = opened["system"];
            var first = opened["mic:a"];
            devices = [new("a", "A", false), new("b", "B", false), new("loop", "Stereo Mix", true)];
            mixer.ScanConnectedInputs();
            Assert.Equal(3, mixer.Sources.Count);
            Assert.Same(first, opened["mic:a"]);
            Assert.False(first.Disposed);
            mixer.SetMuted("mic:b", true);
            devices = [new("a", "A", false)];
            mixer.ScanConnectedInputs();
            Assert.True(opened["mic:b"].Disposed);
            devices = [new("a", "A", false), new("b", "B", false)];
            mixer.ScanConnectedInputs();
            Assert.True(mixer.Sources.Single(s => s.DeviceId == "b").Muted);
            devices = [new("a-new", "A", false), new("b", "B", false)];
            mixer.ScanConnectedInputs();
            Assert.True(first.Disposed);
            Assert.Contains(mixer.Sources, s => s.DeviceId == "a-new");
            Assert.DoesNotContain(mixer.Sources, s => s.DeviceId == "a");
            Assert.Same(system, opened["system"]);
            Assert.False(system.Disposed);
        }
        finally { await mixer.StopAsync(); }
    }

    [Fact]
    public async Task Silent_packets_trigger_multiple_reopens_and_audible_signal_resets_schedule()
    {
        var clock = new ManualClock();
        var captures = new ConcurrentQueue<FakeCapture>();
        FakeCapture? current = null;
        using var mixer = new WasapiAudioMixer(new NullLog(), () => [new("a", "A", false)], s =>
        {
            var capture = new FakeCapture();
            if (s.Kind == AudioSourceKind.Microphone) { current = capture; captures.Enqueue(capture); }
            return capture;
        }, clock, enableTimers: false);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            var count = 1;
            foreach (var seconds in new[] { 60, 300, 600 })
            {
                clock.Advance(seconds);
                current!.Emit(0);
                mixer.TickLevels();
                count++;
                await WaitUntil(() => captures.Count == count);
                Assert.True(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            }
            current!.Emit(.25f);
            mixer.TickLevels();
            var before = captures.Count;
            clock.Advance(59);
            current.Emit(0);
            mixer.TickLevels();
            Assert.Equal(before, captures.Count);
            clock.Advance(1);
            current.Emit(0);
            mixer.TickLevels();
            await WaitUntil(() => captures.Count == before + 1);
        }
        finally { await mixer.StopAsync(); }
    }

    [Fact]
    public async Task Missing_packets_are_retried_and_manual_mode_does_not_add_other_devices()
    {
        var clock = new ManualClock();
        var micOpens = 0;
        using var mixer = new WasapiAudioMixer(new NullLog(), () => [new("b", "B", false)], s =>
        {
            if (s.Kind == AudioSourceKind.Microphone) Interlocked.Increment(ref micOpens);
            return new FakeCapture();
        }, clock, enableTimers: false) { AutoIncludeMicrophones = false };
        mixer.ConfigureSources([new("mic:a", AudioSourceKind.Microphone, "a", "A")]);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            mixer.ScanConnectedInputs();
            Assert.DoesNotContain(mixer.Sources, s => s.DeviceId == "b");
            clock.Advance(6);
            mixer.TickLevels();
            await WaitUntil(() => Volatile.Read(ref micOpens) == 2);
            // Read status under the lifecycle lock to wait for publication of the reopened capture.
            Assert.True(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            clock.Advance(31);
            mixer.TickLevels();
            await WaitUntil(() => Volatile.Read(ref micOpens) == 3);
        }
        finally { await mixer.StopAsync(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Capture lifecycle did not finish within two seconds.");
    }

    [Fact]
    public async Task Repeated_driver_open_failures_back_off_then_recover_without_restarting_system()
    {
        var clock = new ManualClock();
        var attempts = 0;
        var system = new FakeCapture();
        using var mixer = new WasapiAudioMixer(new NullLog(), () => [new("a", "A", false)], s =>
        {
            if (s.Kind == AudioSourceKind.System) return system;
            if (Interlocked.Increment(ref attempts) < 4) throw new InvalidOperationException("USB not ready");
            return new FakeCapture();
        }, clock, enableTimers: false);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, attempts);
            mixer.TickLevels();
            await WaitUntil(() => Volatile.Read(ref attempts) == 2);
            Assert.False(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            clock.Advance(29);
            mixer.TickLevels();
            Assert.Equal(2, attempts);
            clock.Advance(1);
            mixer.TickLevels();
            await WaitUntil(() => Volatile.Read(ref attempts) == 3);
            Assert.False(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            clock.Advance(60);
            mixer.TickLevels();
            await WaitUntil(() => Volatile.Read(ref attempts) == 4);
            Assert.True(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            Assert.False(system.Disposed);
        }
        finally { await mixer.StopAsync(); }
    }

    [Fact]
    public async Task Enumeration_failure_keeps_existing_capture_until_discovery_recovers()
    {
        var fail = false;
        using var mixer = new WasapiAudioMixer(new NullLog(), () => fail
            ? throw new InvalidOperationException("Enumeration temporarily failed") : [new("a", "A", false)],
            _ => new FakeCapture(), enableTimers: false);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            fail = true;
            mixer.ScanConnectedInputs();
            Assert.True(mixer.SourceStatuses.Single(s => s.Id == "mic:a").CaptureActive);
            fail = false;
            mixer.ScanConnectedInputs();
            Assert.Contains(mixer.Sources, s => s.Id == "mic:a");
        }
        finally { await mixer.StopAsync(); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool Disposed { get; private set; }
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Dispose() { Disposed = true; StopRecording(); }
        public void Emit(float level)
        {
            var samples = Enumerable.Repeat(level, 960 * 2).ToArray();
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
    }

    private sealed class NullLog : ILogService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
