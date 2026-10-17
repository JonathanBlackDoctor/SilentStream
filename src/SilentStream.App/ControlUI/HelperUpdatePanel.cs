using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Inspection.Installer;

namespace SilentStream.App.ControlUI;

/// <summary>Explicit in-app maintenance; never starts elevation or opens a browser in the background.</summary>
public sealed class HelperUpdatePanel : UserControl
{
    public HelperUpdatePanel(IStreamOrchestrator orchestrator)
    {
        var button = new Button { Content = "도우미 업데이트", MinWidth = 125 };
        var status = new TextBlock { MaxWidth = 240, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        var cancel = new Button { Content = "다운로드 취소", Visibility = Visibility.Collapsed };
        var panel = new StackPanel();
        panel.Children.Add(button); panel.Children.Add(status); panel.Children.Add(cancel);
        Content = panel;
        CancellationTokenSource? cancellation = null;
        cancel.Click += (_, _) => cancellation?.Cancel();
        button.Click += async (_, _) =>
        {
            if (orchestrator.State != StreamState.Idle)
            { status.Text = "방송·녹음이 끝난 뒤 눌러 주세요."; return; }
            button.IsEnabled = false;
            cancel.Visibility = Visibility.Visible;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            cancellation = cts;
            var staging = Path.Combine(Path.GetTempPath(), "SilentStreamHelperUpdate-" + Guid.NewGuid().ToString("N"));

            var launched = false;
            var completed = false;
            try
            {
                Directory.CreateDirectory(staging);
                status.Text = "공식 설치 파일을 내려받고 검증합니다.";
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                var target = Path.Combine(staging, "SilentStream-Helper-Update.exe");
                await OfficialRelease.DownloadAsync(client, OfficialRelease.Version, false, target, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (orchestrator.State != StreamState.Idle)
                { status.Text = "방송·녹음이 시작되어 설치를 보류했습니다. 종료 후 다시 눌러 주세요."; return; }
                cancel.Visibility = Visibility.Collapsed;
                status.Text = "Windows 관리자 승인 후 갱신합니다. 앱이 잠시 재시작됩니다.";
                using var process = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true, Verb = "runas", Arguments = "--update-existing",
                    WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
                launched = true;
                // The verified installer owns its staging once launched; the app may be restarted by installation.
                await process.WaitForExitAsync();
                completed = true;
                status.Text = process.ExitCode == 0 ? "설치 프로그램이 종료되었습니다. 도우미 연결 상태를 확인하세요."
                    : "도우미 업데이트를 완료하지 못했습니다. 설치 안내를 확인하세요.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { status.Text = "관리자 승인을 취소했습니다. 기존 설치를 유지합니다."; }
            catch (OperationCanceledException) { status.Text = "다운로드를 취소했거나 제한 시간이 지났습니다."; }
            catch (Exception) { status.Text = "업데이트를 준비하지 못했습니다. 인터넷 연결과 공식 릴리스 준비 여부를 확인하세요."; }
            finally
            {
                cancellation = null;
                button.IsEnabled = true;
                cancel.Visibility = Visibility.Collapsed;
                // Never delete a running installer's file.
                if (!launched || completed) { try { Directory.Delete(staging, true); } catch { } }
            }
        };
    }
}
