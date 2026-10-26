using SilentStream.Core.Media;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public sealed class MicrophoneRecoveryPolicyTests
{
    private static readonly DateTime Start = DateTime.UnixEpoch;
    private static readonly AudioSourceSettings Mic = new("mic:a", AudioSourceKind.Microphone, "a", "A");

    [Fact]
    public void Failed_opens_cannot_spin_and_continue_retrying_after_five_minute_cap()
    {
        var policy = new MicrophoneRecoveryPolicy(Start);
        var now = Start;
        foreach (var delay in new[] { 30, 60, 120, 300, 300 })
        {
            Assert.True(policy.CanRetryFailure(now));
            policy.Attempt(now);
            now = now.AddSeconds(delay);
            Assert.False(policy.CanRetryFailure(now.AddMilliseconds(-1)));
        }
        Assert.True(policy.CanRetryFailure(now));
    }

    [Fact]
    public void Audible_signal_resets_capture_failure_retry_budget()
    {
        var policy = new MicrophoneRecoveryPolicy(Start);
        policy.Attempt(Start);
        Assert.False(policy.CanRetryFailure(Start.AddSeconds(1)));
        policy.ObserveSignal(Start.AddSeconds(1));
        Assert.True(policy.CanRetryFailure(Start.AddSeconds(1)));
    }

    [Fact]
    public void All_inputs_deduplicates_endpoints_excludes_loopback_and_preserves_user_controls()
    {
        var system = new AudioSourceSettings("system", AudioSourceKind.System, null, "PC", .4, true);
        var muted = Mic with { Muted = true, Gain = .3 };
        var result = MicrophoneRemote.Select([system, muted, Mic with { Id = "old", DeviceId = "old" }],
            [new("a", "A", false), new("a", "Duplicate", false), new("b", "B", false),
             new("loop", "Stereo Mix", false), new("loop2", "Monitor", true)], "*");
        Assert.Equal(3, result.Count);
        Assert.Equal(system, result[0]);
        Assert.Equal(muted, result[1]);
        Assert.Equal("b", result[2].DeviceId);
        Assert.False(result[2].GateEnabled);
    }

    [Fact]
    public void Default_alias_is_replaced_not_mixed_twice_and_no_devices_keeps_waiting_input()
    {
        var fallback = Mic with { Id = "mic:default", DeviceId = null, Muted = true };
        var result = MicrophoneRecoveryPolicy.ConnectedSources([fallback], [new("a", "A", false)]);
        Assert.Equal("a", Assert.Single(result).DeviceId);
        Assert.True(result[0].Muted);
        Assert.Null(Assert.Single(MicrophoneRecoveryPolicy.ConnectedSources([], [])).DeviceId);
    }
}
