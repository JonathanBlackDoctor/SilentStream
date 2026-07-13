using SilentStream.App.Provisioning;
using System.IO;
using SilentStream.Core;
using SilentStream.Core.Provisioning;
using SilentStream.Core.Recovery;

namespace SilentStream.App.Recovery;

/// <summary>Runs before WPF/DI configuration reads so a missing AppData directory can be restored.</summary>
public static class RecoveryBootstrap
{
    public static bool TryRestore()
    {
        if (File.Exists(AppPaths.ConfigFile)) return false;
        var bootstrap = ProvisioningBootstrapLoader.TryLoad();
        if (bootstrap is null) return false;
        try
        {
            var keys = new CngRecoveryKeyStore();
            var identity = keys.TryOpen();
            if (identity is null) return false;
            using var client = new RecoveryVaultClient(bootstrap.ServiceUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var challenge = client.GetChallengeAsync(identity.KeyId, cts.Token).GetAwaiter().GetResult();
            if (challenge is null) return false;
            var snapshot = client.RestoreAsync(challenge, keys.Sign(challenge.Nonce), cts.Token).GetAwaiter().GetResult();
            if (snapshot is null) return false;
            RecoveryArchive.Restore(AppPaths.AppDataDir, snapshot, keys);
            return File.Exists(AppPaths.ConfigFile);
        }
        catch
        {
            // Recovery is intentionally fail-safe: first-run provisioning remains available when a
            // server is temporarily unavailable or an archive fails verification.
            return false;
        }
    }
}
