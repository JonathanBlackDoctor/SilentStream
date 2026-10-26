using SilentStream.Core.Media;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class MicrophoneInputHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly AudioSourceSettings Mic = new("mic:a", AudioSourceKind.Microphone, "a", "USB Mic");
    private static AudioSourceRuntimeStatus Healthy => new("mic:a", true, null, Now.AddMinutes(-1), Now, 4096);

    [Fact]
    public void Healthy_quiet_packets_are_informational_and_audible_signal_is_separate()
    {
        var quiet = MicrophoneInputHealth.Evaluate(Mic, Healthy, false, Now);
        Assert.Equal("quiet", quiet.Code);
        Assert.False(quiet.IsFailure);
        Assert.Contains("데이터 정상", quiet.Message);
        Assert.Equal("signal", MicrophoneInputHealth.Evaluate(Mic, Healthy, true, Now).Code);
        Assert.Equal("muted", MicrophoneInputHealth.Evaluate(Mic with { Muted = true }, Healthy, false, Now).Code);
        Assert.Equal("muted", MicrophoneInputHealth.Evaluate(Mic with { Gain = 0 }, Healthy, false, Now).Code);
    }

    [Theory]
    [InlineData(AudioCaptureFailure.DeviceUnavailable, "deviceUnavailable")]
    [InlineData(AudioCaptureFailure.DataStopped, "dataStopped")]
    [InlineData(AudioCaptureFailure.CaptureError, "captureError")]
    [InlineData(AudioCaptureFailure.None, "captureError")]
    public void Endpoint_packet_and_capture_failures_are_distinct_even_when_app_is_muted(AudioCaptureFailure failure, string code)
    {
        var runtime = new AudioSourceRuntimeStatus("mic:a", false, "Failure", Now, Failure: failure);
        var input = MicrophoneInputHealth.Evaluate(Mic with { Muted = true }, runtime, false, Now);
        Assert.Equal(code, input.Code);
        Assert.True(input.IsFailure);
        if (failure != AudioCaptureFailure.None)
            Assert.True(MicrophoneInputHealth.Evaluate(Mic, runtime with { Error = null }, false, Now).IsFailure);
    }

    [Fact]
    public void Stale_packets_and_absent_first_packet_have_grace_without_claiming_a_bad_cable()
    {
        var starting = Healthy with { LastPacketAtUtc = null, UpdatedAtUtc = Now };
        Assert.Equal("starting", MicrophoneInputHealth.Evaluate(Mic, starting, true, Now).Code);
        Assert.Equal("dataStopped", MicrophoneInputHealth.Evaluate(Mic, starting, false, Now.AddSeconds(6)).Code);
        var lost = MicrophoneInputHealth.Evaluate(Mic, Healthy, false, Now.AddSeconds(6));
        Assert.True(lost.IsFailure);
        Assert.Contains("원인은 아직 확정되지", lost.Message);
        Assert.Equal("inactive", MicrophoneInputHealth.Evaluate(Mic, Healthy with { CaptureActive = false }, false, Now).Code);
        Assert.Equal("inactive", MicrophoneInputHealth.Evaluate(Mic, null, false, Now).Code);
    }

    [Fact]
    public void Loopback_silence_is_never_a_physical_microphone_failure()
    {
        var state = MicrophoneInputHealth.Evaluate(Mic, Healthy with { IsLoopback = true, CaptureActive = false }, false, Now);
        Assert.Equal("loopback", state.Code);
        Assert.False(state.IsFailure);
    }
}
