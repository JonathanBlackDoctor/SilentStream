using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Supplies the application server's VAPID keypair (원격 컨트롤러 개선 Phase 3), generating it on
/// first use and persisting it so the browser's <c>applicationServerKey</c> stays stable across
/// restarts — a changed key invalidates every existing subscription.
/// </summary>
public interface IVapidKeyStore
{
    /// <summary>Returns the persisted keypair, creating and saving one on first call.</summary>
    VapidKeys GetOrCreate();

    /// <summary>
    /// Atomically installs a VAPID keypair received from trusted room provisioning. Invalid or
    /// mismatched P-256 material is rejected; callers clear stale browser subscriptions only
    /// when the returned result is <see cref="VapidKeyInstallResult.Replaced"/>.
    /// </summary>
    VapidKeyInstallResult Install(VapidKeys keys);
}

/// <summary>Outcome of installing provisioning-supplied VAPID material.</summary>
public enum VapidKeyInstallResult
{
    Invalid,
    Unchanged,
    Replaced
}
