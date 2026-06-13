using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SilentStream.Core.Models;

namespace SilentStream.App.StatusIndicator;

/// <summary>
/// The 9px status box (plan §3.2): top-left of the primary monitor, TopMost, static,
/// click-through, hidden from Alt+Tab/taskbar via WS_EX_TRANSPARENT|LAYERED|TOOLWINDOW.
/// 🟢 live / 🟡 connecting / 🔴 error-or-stopped.
/// </summary>
public partial class StatusBoxWindow : Window
{
    private const int GwlExStyle = -20;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x40));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));

    public StatusBoxWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle,
            styles | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>Maps the stream state to the box colour (plan §4.1 state machine).</summary>
    public void SetState(StreamState state) => Dispatcher.Invoke(() =>
    {
        StateRect.Fill = state switch
        {
            StreamState.Live => GreenBrush,
            StreamState.Warmup or StreamState.ConnectingYouTube => YellowBrush,
            _ => RedBrush
        };
    });

    /// <summary>Optional 1px purple corner marker when recording has failed (plan §3.2).</summary>
    public void SetRecordingFailed(bool failed) => Dispatcher.Invoke(() =>
        RecordingFailDot.Visibility = failed ? Visibility.Visible : Visibility.Collapsed);

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint SetWindowLong(IntPtr hwnd, int index, uint newStyle);
}
