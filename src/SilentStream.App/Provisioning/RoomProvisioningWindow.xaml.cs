using System.Windows;
using System.Windows.Controls;
using SilentStream.Core.Provisioning;

namespace SilentStream.App.Provisioning;

/// <summary>
/// First-install room picker. It never writes configuration itself; the application host receives
/// <see cref="Assignment"/> and immediately encrypts the one-room token through DPAPI.
/// </summary>
public partial class RoomProvisioningWindow : Window
{
    private readonly RoomProvisioningClient _client;
    private readonly string _installationId;
    private bool _requiresActivationCode;
    private bool _loaded;

    public ProvisioningAssignment? Assignment { get; private set; }

    public RoomProvisioningWindow(RoomProvisioningClient client, string installationId)
    {
        _client = client;
        _installationId = installationId;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        StatusText.Text = "호실 목록을 불러오는 중입니다…";
        try
        {
            var catalog = await _client.GetCatalogAsync();
            _requiresActivationCode = catalog.RequiresActivationCode;
            CodePanel.Visibility = _requiresActivationCode ? Visibility.Visible : Visibility.Collapsed;
            RoomCombo.ItemsSource = catalog.Rooms;
            RoomCombo.SelectedIndex = 0;
            StatusText.Text = _requiresActivationCode
                ? "서버 정책상 호실별 등록 코드가 필요합니다."
                : "호실을 선택하면 원격접속 설정이 자동으로 적용됩니다.";
            UpdateApplyEnabled();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"호실 목록을 불러오지 못했습니다: {ex.Message}";
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (RoomCombo.SelectedItem is not ProvisioningRoom room)
        {
            return;
        }

        ApplyButton.IsEnabled = false;
        StatusText.Text = "선택한 호실의 설정을 안전하게 적용하는 중입니다…";
        try
        {
            Assignment = await _client.ClaimAsync(new ProvisioningClaimRequest(
                room.Id, _installationId, Environment.MachineName,
                _requiresActivationCode ? ActivationCodeBox.Password : null));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            UpdateApplyEnabled();
        }
    }

    private void OnRoomChanged(object sender, SelectionChangedEventArgs e) => UpdateApplyEnabled();

    private void OnCodeChanged(object sender, RoutedEventArgs e) => UpdateApplyEnabled();

    private void OnLater(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateApplyEnabled() =>
        ApplyButton.IsEnabled = RoomCombo.SelectedItem is ProvisioningRoom &&
            (!_requiresActivationCode || !string.IsNullOrWhiteSpace(ActivationCodeBox.Password));
}
