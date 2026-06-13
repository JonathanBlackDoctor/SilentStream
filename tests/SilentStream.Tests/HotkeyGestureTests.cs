using SilentStream.Core.Hotkeys;
using Xunit;

namespace SilentStream.Tests;

public class HotkeyGestureTests
{
    [Fact]
    public void Parses_the_default_gesture()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+F12", out var gesture));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, gesture!.Modifiers);
        Assert.Equal(0x7Bu, gesture.VirtualKey); // VK_F12
        Assert.Equal("Ctrl+Shift+F12", gesture.Display);
    }

    [Theory]
    [InlineData("ctrl + alt + s", HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)'S')]
    [InlineData("Win+F1", HotkeyModifiers.Win, 0x70u)]
    [InlineData("Shift+9", HotkeyModifiers.Shift, (uint)'9')]
    [InlineData("F24", HotkeyModifiers.None, 0x87u)]
    public void Parses_case_and_spacing_variants(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(mods, gesture!.Modifiers);
        Assert.Equal(vk, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]      // no key
    [InlineData("Ctrl+F1+F2")]      // two keys
    [InlineData("Ctrl+Escape")]     // unsupported key name
    [InlineData("F25")]             // out of range
    public void Rejects_invalid_gestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Display_is_normalized_for_storage()
    {
        Assert.True(HotkeyGesture.TryParse("shift + ctrl + f5", out var gesture));
        Assert.Equal("Ctrl+Shift+F5", gesture!.Display);
    }
}
