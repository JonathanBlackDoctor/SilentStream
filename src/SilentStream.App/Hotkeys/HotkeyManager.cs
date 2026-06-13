using System.Runtime.InteropServices;
using System.Windows.Interop;
using SilentStream.Core.Contracts;
using SilentStream.Core.Hotkeys;

namespace SilentStream.App.Hotkeys;

/// <summary>
/// Global hotkey registration via RegisterHotKey (plan §3.8). Uses a message-only
/// window so the hotkey works while every app window is hidden.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB00B;

    private readonly ILogService _log;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyManager(ILogService log) => _log = log;

    /// <summary>Registers (or re-registers) the gesture; returns false if taken/invalid.</summary>
    public bool Register(string gestureText)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
        {
            _log.Warn($"단축키 형식이 올바르지 않습니다: \"{gestureText}\"");
            return false;
        }

        EnsureMessageWindow();
        Unregister();

        if (!RegisterHotKey(_source!.Handle, HotkeyId, (uint)gesture!.Modifiers, gesture.VirtualKey))
        {
            _log.Warn($"단축키 등록 실패(다른 프로그램이 사용 중일 수 있음): {gesture.Display}");
            return false;
        }

        _registered = true;
        _log.Info($"전역 단축키 등록: {gesture.Display}");
        return true;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    private void EnsureMessageWindow()
    {
        if (_source is not null)
        {
            return;
        }
        // HWND_MESSAGE(-3) parent: invisible window that only receives messages.
        var parameters = new HwndSourceParameters("SilentStreamHotkeySink")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
