using SilentStream.Core.Contracts;
using Squirrel;

namespace SilentStream.App.Updates;

/// <summary>
/// Squirrel auto-update (plan §3.11): checks the release feed periodically, applies
/// updates in the background, and logs that the new version takes effect on the next
/// start (i.e. the next boot of this always-on app). Uses Clowd.Squirrel, the
/// maintained .NET 8 continuation of Squirrel.Windows.
/// </summary>
public sealed class AppUpdateManager(ILogService log) : IDisposable
{
    // 배포 피드 URL — 릴리스 인프라 확정 시 교체 (예: GitHub Releases 또는 정적 호스팅).
    private const string UpdateUrl = "https://example.com/silentstream/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private CancellationTokenSource? _cts;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync().ConfigureAwait(false);
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

    private async Task CheckOnceAsync()
    {
        try
        {
            using var manager = new UpdateManager(UpdateUrl);
            if (!manager.IsInstalledApp)
            {
                return; // dev run (not a Squirrel install) — nothing to update
            }

            var release = await manager.UpdateApp().ConfigureAwait(false);
            if (release is not null)
            {
                log.Info($"업데이트 적용됨: v{release.Version} — 다음 시작 시 반영됩니다.");
            }
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
