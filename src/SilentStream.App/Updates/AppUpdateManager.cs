using SilentStream.Core.Contracts;
using System.Net.Http;
using Velopack;
using Velopack.Sources;
using SilentStream.Inspection.Installer;

namespace SilentStream.App.Updates;

/// <summary>
/// Velopack 자동 업데이트 (plan §3.11): GitHub Releases 피드를 6시간마다 확인하고, 새 버전이 있으면
/// 백그라운드로 내려받아 <b>다음 종료/재시작 시 적용</b>되도록 예약한다(라이브 송출 중에는 강제 재시작하지
/// 않는다). 개발 실행(미설치)·네트워크 실패는 조용히 건너뛴다. Clowd.Squirrel의 후속작 Velopack 사용.
/// </summary>
public sealed class AppUpdateManager(ILogService log) : IDisposable
{
    // The enrolled coordinator registers this PC's public key; GitHub credentials stay on the server.
    private static UpdateManager CreateManager() => new(new SimpleWebSource(PrivateUpdateIdentity.BaseUrl, new PrivateUpdateDownloader()));

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private CancellationTokenSource? _cts;
    private string? _stagedVersion; // 이미 내려받아 적용 예약한 버전 — 중복 다운로드/대기 프로세스 방지.
    private DateTimeOffset? _lastCheckUtc;
    private int _registrationRetries;
    private bool _awaitingRegistration;

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
            if (_lastCheckUtc is { } last)
            {
                var remaining = (_awaitingRegistration ? TimeSpan.FromSeconds(30) : CheckInterval) - (DateTimeOffset.UtcNow - last);
                if (remaining > TimeSpan.Zero)
                {
                    try { await Task.Delay(remaining, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            await CheckOnceAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(_awaitingRegistration ? TimeSpan.FromSeconds(30) : CheckInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Performs the one update check that must precede unattended recording and inspection. When
    /// an installed build has a pending/new release, the update is downloaded and a silent
    /// restart is armed; the caller must then shut down the current WPF process. Offline or
    /// development builds continue with the installed version after a bounded wait.
    /// </summary>
    public async Task<bool> PrepareStartupUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                return false;
            }

            var pending = manager.UpdatePendingRestart;
            if (pending is null)
            {
                var update = await manager.CheckForUpdatesAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                _lastCheckUtc = DateTimeOffset.UtcNow;
                if (update is null)
                {
                    log.Info("부팅 업데이트 확인 완료: 최신 버전입니다.");
                    return false;
                }

                using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                await manager.DownloadUpdatesAsync(update, cancelToken: downloadTimeout.Token).ConfigureAwait(false);
                pending = update.TargetFullRelease;
            }

            var version = pending.Version.ToString();
            _stagedVersion = version;
            UpdateStaged?.Invoke(version);
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true);
            log.Info($"부팅 업데이트 준비 완료: v{version} 적용 후 앱을 자동으로 다시 시작합니다.");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _lastCheckUtc = DateTimeOffset.UtcNow;
            _awaitingRegistration = true;
            log.Info("기기 업데이트 인증 등록을 기다립니다. 현재 버전으로 시작합니다.");
            return false;
        }
        catch (TimeoutException)
        {
            _lastCheckUtc = DateTimeOffset.UtcNow;
            log.Warn("부팅 업데이트 확인이 20초를 초과해 현재 버전으로 시작합니다. 다음 주기에 다시 확인합니다.");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastCheckUtc = DateTimeOffset.UtcNow;
            log.Warn("부팅 업데이트 다운로드가 제한시간을 초과해 현재 버전으로 시작합니다. 다음 주기에 다시 확인합니다.");
            return false;
        }
        catch (Exception ex)
        {
            _lastCheckUtc = DateTimeOffset.UtcNow;
            log.Warn($"부팅 업데이트 확인 실패(현재 버전으로 계속): {ex.Message}");
            return false;
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            _lastCheckUtc = DateTimeOffset.UtcNow;
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                return; // 개발 실행(Velopack 설치본 아님) — 업데이트 대상 없음
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            _awaitingRegistration = false;
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized && _registrationRetries++ < 4)
        {
            _awaitingRegistration = true;
        }
        catch (Exception ex)
        {
            _awaitingRegistration = false;
            log.Warn($"업데이트 확인 실패(다음 주기에 재시도): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
