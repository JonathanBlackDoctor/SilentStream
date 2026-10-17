using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SilentStream.Core.UsbDocuments;

namespace SilentStream.App.ControlUI;

public sealed class UsbDocumentsPanel : UserControl
{
    public UsbDocumentsPanel(UsbDocumentArchive archive)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var enabled = new CheckBox { Content = "USB 문서 자동 보관 사용", IsChecked = archive.Settings.Enabled };
        var folder = new TextBox { Text = archive.Settings.Folder, Margin = new Thickness(0, 8, 0, 8) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        var list = new ListBox { Height = 220, DisplayMemberPath = "Label" };
        var save = new Button { Content = "USB 보관 설정 저장", HorizontalAlignment = HorizontalAlignment.Left };
        var retry = new Button { Content = "선택한 실패 파일 재시도", HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "PPT·PPTX·PDF 읽기를 감지합니다. 탐색기 미리보기도 포함될 수 있습니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(folder); panel.Children.Add(save); panel.Children.Add(status); panel.Children.Add(list); panel.Children.Add(retry);
        Content = panel;
        void Refresh()
        {
            var snapshot = archive.Snapshot();
            status.Text = snapshot.DetectorStatus + (snapshot.Dropped > 0 ? $" · 감지 누락 {snapshot.Dropped}건" : "");
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
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { if (IsVisible) Refresh(); };
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }
    private static string State(string state) => state switch
    { "queued" => "대기", "copying" => "복사 중", "ready" => "보관됨", "duplicate" => "중복 내용", _ => "실패" };
    private sealed record Row(string Id, string Label);
}
