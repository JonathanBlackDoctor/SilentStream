using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using SilentStream.App.Provisioning;
using SilentStream.App.Recovery;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.Core.Provisioning;

namespace SilentStream.App.Remote;

/// <summary>Coordinates a recoverable remote removal without allowing deletion to outrun backup.</summary>
public sealed class RemoteUninstallService
{
    private readonly IStreamOrchestrator _orchestrator;
    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _protector;
    private readonly RecoveryCoordinator _recovery;
    private readonly ILogService _log;
    private int _queued;

    public RemoteUninstallService(IStreamOrchestrator orchestrator, IConfigStore configStore, ITokenProtector protector,
        RecoveryCoordinator recovery, ILogService log)
    {
        _orchestrator = orchestrator;
        _configStore = configStore;
        _protector = protector;
        _recovery = recovery;
        _log = log;
    }

    public async Task<RemoteUninstallResult> RequestAsync(string? administratorToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(administratorToken)) return new(false, "관리자 인증이 필요합니다.");
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return new(false, "이미 제거 작업이 진행 중입니다.");
        var launched = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            if (!await _recovery.FlushAsync(timeout.Token).ConfigureAwait(false))
                return new(false, "최신 복구 백업을 확인하지 못해 제거를 중단했습니다.");

            var bootstrap = ProvisioningBootstrapLoader.TryLoad();
            var config = _configStore.Load();
            if (bootstrap is null || !config.Provisioning.Completed || string.IsNullOrWhiteSpace(config.Provisioning.RoomId) ||
                string.IsNullOrWhiteSpace(config.Provisioning.InstallationId))
                return new(false, "관리형 호실 등록이 완료되지 않았습니다.");
            var tunnelToken = string.IsNullOrWhiteSpace(config.Remote.CloudflareTunnelTokenEnc)
                ? config.Remote.CloudflareTunnelToken
                : _protector.Unprotect(config.Remote.CloudflareTunnelTokenEnc);
            if (string.IsNullOrWhiteSpace(tunnelToken)) return new(false, "기기 인증 정보를 읽을 수 없습니다.");

            using var client = new RecoveryVaultClient(bootstrap.ServiceUrl);
            var command = await client.IssueRemovalAsync(config.Provisioning.RoomId, administratorToken, timeout.Token).ConfigureAwait(false);
            var authorization = await client.ConsumeRemovalAsync(config.Provisioning.RoomId, config.Provisioning.InstallationId,
                tunnelToken, command.CommandId, timeout.Token).ConfigureAwait(false);

            await _orchestrator.StopAsync().ConfigureAwait(false);
            new AutoStartManager(_log).DisableAll();
            StartHelper(bootstrap.ServiceUrl, command.CommandId, authorization.CompletionToken);
            launched = true;
            _log.Warn("관리자 원격 요청으로 복구 가능한 완전 제거를 시작합니다.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var app = Application.Current;
                if (app is not null) _ = app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
            });
            return new(true, "최신 백업을 완료했습니다. 연결이 종료되면 앱을 제거합니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(false, "제거 요청 시간이 초과되었습니다.");
        }
        catch (Exception ex)
        {
            _log.Error("원격 제거 요청 실패", ex);
            return new(false, "원격 제거를 시작하지 못했습니다.");
        }
        finally
        {
            // Once the detached helper has launched, the process exits shortly; leaving the gate
            // closed prevents a second destructive command during that hand-off.
            if (!launched) Interlocked.Exchange(ref _queued, 0);
        }
    }

    private static void StartHelper(string serviceUrl, string commandId, string completionToken)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "uninstall-helper", "MediaCaptureHelper.UninstallHelper.exe");
        if (!File.Exists(bundled)) throw new FileNotFoundException("Remote uninstall helper is not bundled.", bundled);
        var directory = Path.Combine(Path.GetTempPath(), "MediaCaptureHelper-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var helper = Path.Combine(directory, Path.GetFileName(bundled));
        File.Copy(bundled, helper, overwrite: true);
        var payload = Path.Combine(directory, "request.json");
        var updater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaCaptureHelper", "Update.exe");
        File.WriteAllText(payload, JsonSerializer.Serialize(new
        {
            parentProcessId = Environment.ProcessId,
            updateExePath = updater,
            appDataDirectory = AppPaths.AppDataDir,
            serviceUrl,
            commandId,
            completionToken
        }));
        if (Process.Start(new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { payload }
        }) is null)
        {
            throw new InvalidOperationException("Remote uninstall helper could not start.");
        }
    }
}

public sealed record RemoteUninstallResult(bool Ok, string Message);
