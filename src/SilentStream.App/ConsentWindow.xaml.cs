using System.Windows;

namespace SilentStream.App;

/// <summary>First-run consent dialog (plan §1.1). Declining exits the app.</summary>
public partial class ConsentWindow : Window
{
    public bool Accepted { get; private set; }

    public ConsentWindow()
    {
        InitializeComponent();
    }

    private void OnAgree(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }
}
