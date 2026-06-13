namespace SilentStream.Core.Hotkeys;

/// <summary>Win32 RegisterHotKey modifier flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0x0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8
}

/// <summary>
/// A parsed global hotkey ("Ctrl+Shift+F12" → modifiers + virtual-key code), kept in
/// Core so config validation and parsing are testable off-Windows (plan §3.8).
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string Display)
{
    public static bool TryParse(string text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key != 0 || !TryParseKey(part, out key))
                    {
                        return false; // two non-modifier keys, or an unknown key name
                    }
                    break;
            }
        }

        if (key == 0)
        {
            return false; // modifiers alone are not a hotkey
        }

        gesture = new HotkeyGesture(modifiers, key, Normalize(modifiers, key));
        return true;
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        var upper = name.ToUpperInvariant();

        // F1..F24 → 0x70..
        if (upper.Length is 2 or 3 && upper[0] == 'F' &&
            int.TryParse(upper[1..], out var f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        // A..Z and 0..9 share their ASCII codes.
        if (upper.Length == 1 && (char.IsAsciiLetterUpper(upper[0]) || char.IsAsciiDigit(upper[0])))
        {
            vk = upper[0];
            return true;
        }
        return false;
    }

    private static string Normalize(HotkeyModifiers modifiers, uint vk)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(vk is >= 0x70 and <= 0x87
            ? $"F{vk - 0x70 + 1}"
            : ((char)vk).ToString());
        return string.Join('+', parts);
    }
}
