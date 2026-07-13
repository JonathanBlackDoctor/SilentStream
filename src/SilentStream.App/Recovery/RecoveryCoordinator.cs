using SilentStream.App.Provisioning;
using System.IO;
using SilentStream.Core;
using SilentStream.Core.Contracts;
using SilentStream.Core.Provisioning;
using SilentStream.Core.Recovery;

namespace SilentStream.App.Recovery;

/// <summary>
/// Watches the small AppData state set and keeps a debounced encrypted server snapshot current.
/// A final FlushAsync is used by remote removal, so uninstall is never allowed to outrun backup.
/// </summary>
public sealed class RecoveryCoordinator : IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _protector;
    private readonly IRecoveryKeyStore _keys;
    private readonly ILogService _log;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly object _stateGate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _periodic;
    private bool _started;
    private RecoveryStatus _status = new(false, null, "복구 백업이 아직 준비되지 않았습니다.");

    public RecoveryCoordinator(IConfigStore configStore, ITokenProtector protector, IRecoveryKeyStore keys, ILogService log)
    {
        _configStore = configStore;
        _protector = protector;
        _keys = keys;
        _log = log;
    }

    public RecoveryStatus Status { get { lock (_stateGate) return _status; } }

    public void Start()
    {
        if (_started || ProvisioningBootstrapLoader.TryLoad() is null) return;
        _started = true;
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            _watcher = new FileSystemWatcher(AppPaths.AppDataDir)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) => RequestBackup();
            _watcher.Created += (_, _) => RequestBackup();
            _watcher.Deleted += (_, _) => RequestBackup();
            _watcher.Renamed += (_, _) => RequestBackup();
        }
        catch (Exception ex)
        {
            _log.Warn($"복구 백업 파일 감시를 시작하지 못했습니다: {ex.Message}");
        }
        _periodic = new Timer(_ => RequestBackup(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        RequestBackup();
    }

    public void RequestBackup()
    {
        if (!_started) return;
        _debounce ??= new Timer(_ => _ = FlushAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounce.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (!_started || ProvisioningBootstrapLoader.TryLoad() is not { } bootstrap) return false;
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _configStore.Load();
            if (!config.Provisioning.Completed || string.IsNullOrWhiteSpace(config.Provisioning.RoomId) ||
                string.IsNullOrWhiteSpace(config.Provisioning.InstallationId))
            {
                SetStatus(false, "호실 등록이 완료되지 않아 복구 백업을 만들 수 없습니다.");
                return false;
            }
            var token = string.IsNullOrWhiteSpace(config.Remote.CloudflareTunnelTokenEnc)
                ? config.Remote.CloudflareTunnelToken
                : _protector.Unprotect(config.Remote.CloudflareTunnelTokenEnc);
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus(false, "기기 터널 인증 정보가 없어 복구 백업을 만들 수 없습니다.");
                return false;
            }

            var identity = _keys.GetOrCreate();
            using var client = new RecoveryVaultClient(bootstrap.ServiceUrl);
            await client.RegisterAsync(new RecoveryRegistrationRequest(
                config.Provisioning.RoomId, config.Provisioning.InstallationId, token, identity), ct).ConfigureAwait(false);
            var snapshot = RecoveryArchive.Create(AppPaths.AppDataDir, _keys, DateTimeOffset.UtcNow);
            await client.UploadAsync(new RecoverySnapshotUploadRequest(
                config.Provisioning.RoomId, config.Provisioning.InstallationId, token, snapshot), ct).ConfigureAwait(false);
            SetStatus(true, "복구 백업이 최신입니다.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(false, "복구 백업 시간이 초과되었습니다.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"복구 백업 실패(다음 변경 또는 주기에서 재시도): {ex.Message}");
            SetStatus(false, "복구 서버와 동기화되지 않았습니다.");
            return false;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _periodic?.Dispose();
        _flushGate.Dispose();
    }

    private void SetStatus(bool ready, string message)
    {
        lock (_stateGate)
        {
            _status = new RecoveryStatus(ready, ready ? DateTimeOffset.UtcNow : _status.LastSuccessfulBackupUtc, message);
        }
    }
}
