using System;
using System.Diagnostics;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.App.Recovery;
using Velopack;

namespace SilentStream.App;

/// <summary>
/// 프로세스 진입점. Velopack 후크(설치/업데이트/제거)는 동일 exe를 특수 인자로 재실행하므로,
/// WPF·단일 인스턴스 가드보다 <b>먼저</b> <see cref="VelopackApp"/>를 실행해야 한다. 그래서 App.xaml이
/// 자동 생성하는 Main 대신 이 커스텀 Main을 진입점으로 쓴다(csproj의 StartupObject).
/// 일반 실행에서는 <see cref="VelopackApp.Run"/>이 즉시 반환하고 평소처럼 WPF 호스트가 뜬다.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            // 제거 시 자동 시작 등록과 방화벽 규칙을 정리한다(기존 Inno [UninstallRun] 동등).
            // *FastCallback 후크는 DI 컨테이너 밖에서 짧게 돌고 즉시 종료된다.
            .OnBeforeUninstallFastCallback(_ => CleanupOnUninstall())
            .Run();

        // 리브랜드(SilentStream → Media Capture Helper) 후 기존 설치본의 %AppData% 데이터를 1회 이전한다.
        // Velopack 후크 호출은 위 .Run() 안에서 종료되므로 이 줄은 정상 실행 경로에서만 도달한다.
        AppPaths.MigrateLegacyAppDataIfNeeded();
        // A recoverable remote uninstall deletes AppData, but leaves the named Windows CNG/TPM
        // key. Restore before WPF creates ConfigStore so every normal startup sees one coherent
        // configuration, never a temporary first-run default.
        RecoveryBootstrap.TryRestore();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>제거 직전 best-effort 정리: 자동 시작 해제 + 원격 제어 방화벽 규칙 삭제.</summary>
    private static void CleanupOnUninstall()
    {
        var log = new NullLog();
        try
        {
            new AutoStartManager(log).DisableAll();
        }
        catch
        {
            // best-effort — 제거는 계속 진행되어야 한다.
        }

        try
        {
            // 원격 제어 인바운드 규칙 제거(기본 포트 8787; Inno와 동일하게 best-effort).
            Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall delete rule name=\"MediaCaptureHelper Remote 8787\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(10_000);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>제거 후크는 DI/NLog 초기화 전에 돌 수 있으므로 쓰는 무동작 로거.</summary>
    private sealed class NullLog : ILogService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
