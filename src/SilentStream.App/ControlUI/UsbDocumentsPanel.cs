using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SilentStream.Core.UsbDocuments;

namespace SilentStream.App.ControlUI;

public sealed class UsbDocumentsPanel : UserControl
{
    public UsbDocumentsPanel(UsbDocumentArchive archive, DocumentDriveUpload driveUpload)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var enabled = new CheckBox { Content = "USB 문서 자동 보관 사용", IsChecked = archive.Settings.Enabled };
        var folder = new TextBox { Text = archive.Settings.Folder, Margin = new Thickness(0, 8, 0, 8) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var list = new ListBox { Height = 220, DisplayMemberPath = "Label" };
        var save = new Button { Content = "USB 보관 설정 저장", HorizontalAlignment = HorizontalAlignment.Left };
        var retry = new Button { Content = "선택한 실패 파일 재시도", HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "PPT·PPTX·PDF 읽기를 감지합니다. 탐색기 미리보기도 포함될 수 있습니다. 로컬 보관본은 7일 후 자동 삭제하며 Drive 파일은 유지합니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(folder); panel.Children.Add(save); panel.Children.Add(status); panel.Children.Add(list); panel.Children.Add(retry);
        var driveEnabled = new CheckBox { Content = "보관된 자료를 Google Drive에 자동 업로드", IsChecked = driveUpload.Settings.Enabled, Margin = new Thickness(0, 16, 0, 4) };
        var account = new TextBox { Text = driveUpload.Settings.Account, Margin = new Thickness(0, 4, 0, 4) };
        var driveFolder = new TextBox { Text = driveUpload.Settings.FolderId, Margin = new Thickness(0, 4, 0, 4) };
        var driveStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var connect = new Button { Content = "Drive 계정 연결 및 설정 저장", HorizontalAlignment = HorizontalAlignment.Left };
        var retryDrive = new Button { Content = "실패한 Drive 업로드 재시도", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        string? connectionError = null;
        panel.Children.Add(driveEnabled);
        panel.Children.Add(new TextBlock { Text = "업로드 계정 이메일" }); panel.Children.Add(account);
        panel.Children.Add(new TextBlock { Text = "해당 계정이 소유한 Drive 폴더 링크" }); panel.Children.Add(driveFolder);
        panel.Children.Add(new TextBlock { Text = "연결 시 Drive 파일 접근 권한을 요청합니다. 지정한 계정·폴더를 확인한 뒤 업로드합니다. 로컬 보관본은 업로드 성공 여부와 관계없이 7일 후 삭제됩니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(connect); panel.Children.Add(retryDrive); panel.Children.Add(driveStatus);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Refresh()
        {
            var snapshot = archive.Snapshot();
            status.Text = snapshot.DetectorStatus + (snapshot.Dropped > 0 ? $" · 감지 누락 {snapshot.Dropped}건" : "");
            if (connect.IsEnabled) driveStatus.Text = connectionError ?? driveUpload.Status;
            var selected = (list.SelectedItem as Row)?.Id;
            list.ItemsSource = snapshot.Items.Select(i => new Row(i.Id,
                $"{i.Date}  {i.Name} — {State(i.Status)} {i.CopiedBytes / 1048576d:0.0}/{i.Size / 1048576d:0.0} MiB {i.Error}")).ToArray();
            if (selected is not null) list.SelectedItem = list.Items.Cast<Row>().FirstOrDefault(r => r.Id == selected);
        }
        save.Click += (_, _) =>
        {
            try { archive.Configure(enabled.IsChecked == true, folder.Text); Refresh(); }
            catch (Exception) { status.Text = "저장 폴더 경로와 접근 권한을 확인하세요."; }
        };
        retry.Click += (_, _) => { if (list.SelectedItem is Row row) archive.Retry(row.Id); Refresh(); };
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false; retryDrive.IsEnabled = false;
            connectionError = null;
            driveStatus.Text = "Drive 연결 확인 중 · 브라우저에서 업로드 계정을 선택하세요.";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await driveUpload.ConfigureAsync(driveEnabled.IsChecked == true, account.Text, driveFolder.Text, timeout.Token);
                driveStatus.Text = driveUpload.Status;
            }
            catch (Exception ex)
            {
                connectionError = ex is DriveDocumentException or ArgumentException ? ex.Message
                    : "Drive 연결 실패. 계정·권한·네트워크·Google Drive API 사용 설정을 확인하세요.";
                driveStatus.Text = connectionError;
            }
            finally { connect.IsEnabled = true; retryDrive.IsEnabled = true; }
        };
        retryDrive.Click += (_, _) => { driveUpload.RetryFailed(); Refresh(); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { if (IsVisible) Refresh(); };
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }
    private static string State(string state) => state switch
    { "queued" => "대기", "copying" => "복사 중", "ready" => "보관됨", "duplicate" => "중복 내용", _ => "실패" };
    private sealed record Row(string Id, string Label);
}
