using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

namespace SilentStream.App.ControlUI;

/// <summary>
/// The hotkey-toggled control window. Closing hides instead of disposing so the
/// hotkey can bring it straight back (the app itself stays headless).
/// </summary>
public partial class ControlWindow : Window
{
    public ControlWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Auto-scroll the log viewer to the newest line.
        viewModel.LogLines.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // hide, don't tear down — the app lives in the background
        Hide();
    }
}
