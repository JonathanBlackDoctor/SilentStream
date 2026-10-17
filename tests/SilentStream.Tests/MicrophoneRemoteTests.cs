using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public sealed class MicrophoneRemoteTests
{
    private static readonly AudioSourceSettings SystemSource = new("system", AudioSourceKind.System, null, "PC", 0.4, true);
    private static readonly AudioSourceSettings Mic = new("mic:a", AudioSourceKind.Microphone, "a", "Mic", 0, true, true);

    [Fact]
    public void Select_replaces_microphones_but_preserves_system_settings()
    {
        var result = MicrophoneRemote.Select([SystemSource, Mic], [new("b", "USB", false)], "b");
        Assert.Equal(SystemSource, result[0]);
        Assert.Equal("mic:b", result[1].Id);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[1].Gain);
    }

    [Fact]
    public void Select_existing_preserves_mute_and_gain_until_explicit_restore()
    {
        var result = MicrophoneRemote.Select([SystemSource, Mic], [new("a", "Mic", false)], "a");
        Assert.Equal(Mic, result[1]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("loopback")]
    [InlineData("")]
    public void Select_rejects_disconnected_or_loopback_device(string id) =>
        Assert.Throws<ArgumentException>(() => MicrophoneRemote.Select([Mic], [new("loopback", "Stereo Mix", true)], id));

    [Fact]
    public void Restore_only_changes_blocked_microphone_controls_and_retains_positive_gain()
    {
        var result = MicrophoneRemote.RestoreInput([SystemSource, Mic, Mic with { Id = "mic:b", Gain = 2 }]);
        Assert.Equal(SystemSource, result[0]);
        Assert.False(result[1].Muted);
        Assert.False(result[1].GateEnabled);
        Assert.Equal(1, result[1].Gain);
        Assert.Equal(2, result[2].Gain);
    }
}
