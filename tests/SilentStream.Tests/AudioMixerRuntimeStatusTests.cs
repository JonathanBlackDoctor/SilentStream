using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class AudioMixerRuntimeStatusTests
{
    [Fact]
    public void Non_windows_mixer_keeps_every_configured_source_visible_as_unavailable()
    {
        using var mixer = new AudioMixer();
        var sources = new[]
        {
            new AudioSourceSettings("system", AudioSourceKind.System, null, "시스템 오디오"),
            new AudioSourceSettings("mic:m113", AudioSourceKind.Microphone, "m113-device", "M113 마이크")
        };

        mixer.ConfigureSources(sources);
        var before = DateTime.UtcNow;
        var statuses = mixer.SourceStatuses;
        var after = DateTime.UtcNow;

        Assert.Equal(sources.Select(source => source.Id), statuses.Select(status => status.Id));
        Assert.All(statuses, status =>
        {
            Assert.False(status.CaptureActive);
            Assert.False(string.IsNullOrWhiteSpace(status.Error));
            Assert.InRange(status.UpdatedAtUtc, before, after);
            Assert.Null(status.LastPacketAtUtc);
            Assert.Equal(0, status.BytesReceived);
            Assert.Null(status.ResolvedDeviceId);
            Assert.Null(status.ResolvedDeviceName);
            Assert.False(status.IsLoopback);
        });
    }

    [Fact]
    public void Runtime_status_preserves_packet_health_telemetry()
    {
        var packetAt = DateTime.UtcNow.AddMilliseconds(-25);
        var status = new AudioSourceRuntimeStatus(
            "mic:m113", true, null, DateTime.UtcNow, packetAt, 4096,
            "endpoint:stereo-mix", "Stereo Mix (Realtek Audio)", true);

        Assert.True(status.CaptureActive);
        Assert.Null(status.Error);
        Assert.Equal(packetAt, status.LastPacketAtUtc);
        Assert.Equal(4096, status.BytesReceived);
        Assert.Equal("endpoint:stereo-mix", status.ResolvedDeviceId);
        Assert.Equal("Stereo Mix (Realtek Audio)", status.ResolvedDeviceName);
        Assert.True(status.IsLoopback);
    }
}
