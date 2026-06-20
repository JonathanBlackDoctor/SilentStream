using SilentStream.Core.Contracts;
using Velopack;
using Velopack.Sources;

namespace SilentStream.App.Updates;

/// <summary>
/// Velopack 자동 업데이트 (plan §3.11): GitHub Releases 피드를 6시간마다 확인하고, 새 버전이 있으면
/// 백그라운드로 내려받아 <b>다음 종료/재시작 시 적용</b>되도록 예약한다(라이브 송출 중에는 강제 재시작하지
/// 않는다). 개발 실행(미설치)·네트워크 실패는 조용히 건너뛴다. Clowd.Squirrel의 후속작 Velopack 사용.
/// </summary>
public sealed class AppUpdateManager(ILogService log) : IDisposable
{
    // GitHub Releases 피드(공개 리포라 토큰 불필요). CI의 `vpk upload github` 가 게시하는 릴리스를 읽는다.
    private const string RepoUrl = "https://github.com/JonathanBlackDoctor/SilentStream";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private CancellationTokenSource? _cts;
    private string? _stagedVersion; // 이미 내려받아 적용 예약한 버전 — 중복 다운로드/대기 프로세스 방지.

    /// <summary>
    /// 새 버전을 내려받아 다음 종료/재시작 때 적용되도록 예약한 직후 발생(인자 = 적용 예정 버전, 예 "0.3.0").
    /// 제어창 상태바가 "재시작 시 적용" 안내를 띄우는 데 쓴다. UI 스레드 마샬링은 구독자(App→ViewModel)가 담당.
    /// </summary>
    public event Action<string>? UpdateStaged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(CheckInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                return; // 개발 실행(Velopack 설치본 아님) — 업데이트 대상 없음
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return; // 최신 상태
            }

            var version = update.TargetFullRelease.Version.ToString();
            if (version == _stagedVersion)
            {
                return; // 이미 내려받아 적용 대기 중 — 다음 종료 때 반영된다.
            }

            await manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);
            // 라이브 송출을 끊지 않도록 즉시 재시작하지 않고, 앱/PC가 다음에 종료될 때 적용되도록 예약한다.
            manager.WaitExitThenApplyUpdates(update, silent: true, restart: false);
            _stagedVersion = version;
            log.Info($"업데이트 다운로드 완료: v{version} — PC를 끄거나 앱을 재시작하면 적용됩니다.");
            UpdateStaged?.Invoke(version);
        }
        catch (Exception ex)
        {
            log.Warn($"업데이트 확인 실패(다음 주기에 재시도): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
