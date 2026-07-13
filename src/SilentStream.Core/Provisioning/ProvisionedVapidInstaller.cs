using SilentStream.Core.Contracts;
using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Provisioning;

/// <summary>
/// Applies trusted provisioning VAPID material and preserves the key-rotation invariant: browser
/// subscriptions encrypted to a replaced application-server key are removed as one operation.
/// </summary>
public static class ProvisionedVapidInstaller
{
    public static VapidKeyInstallResult Install(
        IVapidKeyStore keys,
        IPushSubscriptionStore subscriptions,
        VapidKeys provisioned,
        out int removedSubscriptions)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(provisioned);

        var result = keys.Install(provisioned);
        removedSubscriptions = result == VapidKeyInstallResult.Replaced ? subscriptions.Clear() : 0;
        return result;
    }
}
