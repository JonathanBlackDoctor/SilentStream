using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Media.Windows;
using Xunit;

namespace SilentStream.Media.Windows.Tests;

public sealed class WasapiAudioMixerRuntimeStatusTests
{
    [Fact]
    public void Refresh_does_not_start_stopped_capture()
    {
        using var mixer = new WasapiAudioMixer(new NullLog()) { AutoIncludeMicrophones = false };
        mixer.ConfigureSources([new("mic:missing", AudioSourceKind.Microphone, "missing", "Mic")]);
        Assert.False(mixer.RefreshMicrophones());
        Assert.All(mixer.SourceStatuses, s => Assert.False(s.CaptureActive));
    }

    [Fact]
    public async Task Missing_configured_microphone_remains_visible_and_raises_unavailable_signal()
    {
        using var mixer = new WasapiAudioMixer(new NullLog()) { AutoIncludeMicrophones = false };
        var id = "mic:missing-" + Guid.NewGuid().ToString("N");
        var endpoint = "missing-endpoint-" + Guid.NewGuid().ToString("N");
        const string name = "M113 classroom microphone";
        mixer.ConfigureSources([
            new AudioSourceSettings(id, AudioSourceKind.Microphone, endpoint, name)
        ]);
        MicSignalStatus? signal = null;
        var signalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mixer.MicSignalChanged += (_, status) =>
        {
            if (status.SourceId == id) { signal = status; signalReceived.TrySetResult(); }
        };

        await mixer.StartAsync(CancellationToken.None);
        try
        {
            await signalReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var runtime = Assert.Single(mixer.SourceStatuses, status => status.Id == id);
            Assert.False(runtime.CaptureActive);
            Assert.False(string.IsNullOrWhiteSpace(runtime.Error));
            Assert.Equal(AudioCaptureFailure.DeviceUnavailable, runtime.Failure);
            Assert.Null(runtime.LastPacketAtUtc);
            Assert.Equal(0, runtime.BytesReceived);
            Assert.Equal(endpoint, runtime.ResolvedDeviceId);
            Assert.Equal(name, runtime.ResolvedDeviceName);
            Assert.False(runtime.IsLoopback);
            Assert.Contains(mixer.Sources, source => source.Id == id);
            Assert.NotNull(signal);
            Assert.False(signal!.SignalPresent);
            Assert.True(mixer.RefreshMicrophones());
            var refreshed = Assert.Single(mixer.SourceStatuses, s => s.Id == id);
            Assert.False(refreshed.CaptureActive);
            Assert.NotNull(refreshed.Error);
        }
        finally
        {
            await mixer.StopAsync();
        }
    }

    [Fact]
    public async Task Loopback_microphone_status_is_identified_and_never_raises_a_physical_mic_signal_alarm()
    {
        using var mixer = new WasapiAudioMixer(new NullLog()) { AutoIncludeMicrophones = false };
        var id = "mic:loopback-" + Guid.NewGuid().ToString("N");
        var endpoint = "missing-loopback-" + Guid.NewGuid().ToString("N");
        const string name = "Stereo Mix (Realtek Audio)";
        mixer.ConfigureSources([
            new AudioSourceSettings(id, AudioSourceKind.Microphone, endpoint, name)
        ]);
        MicSignalStatus? signal = null;
        mixer.MicSignalChanged += (_, status) =>
        {
            if (status.SourceId == id) signal = status;
        };

        await mixer.StartAsync(CancellationToken.None);
        try
        {
            var runtime = Assert.Single(mixer.SourceStatuses, status => status.Id == id);
            Assert.False(runtime.CaptureActive);
            Assert.False(string.IsNullOrWhiteSpace(runtime.Error));
            Assert.Equal(endpoint, runtime.ResolvedDeviceId);
            Assert.Equal(name, runtime.ResolvedDeviceName);
            Assert.True(runtime.IsLoopback);
            Assert.Null(signal);
        }
        finally
        {
            await mixer.StopAsync();
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
