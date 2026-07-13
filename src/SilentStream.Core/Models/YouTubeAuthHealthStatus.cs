namespace SilentStream.Core.Models;

/// <summary>
/// The current operator-facing state of the OAuth credential shared by YouTube live streaming and
/// VOD uploads. It intentionally contains no access token, refresh token, or server error detail.
/// </summary>
public enum YouTubeAuthHealthState
{
    /// <summary>The last observed credential is outside the warning window and has no known failure.</summary>
    Healthy,

    /// <summary>The access token will expire soon; automatic refresh should be watched.</summary>
    Expiring,

    /// <summary>The credential or OAuth client setup needs an operator action before it can refresh.</summary>
    ActionRequired
}

/// <summary>
/// A safe, user-facing OAuth health snapshot. <see cref="Message"/> is suitable for logs, the
/// phone status endpoint, and notifications; it never contains a secret or raw provider response.
/// </summary>
public sealed record YouTubeAuthHealthStatus(
    YouTubeAuthHealthState State,
    string Message);

/// <summary>Permanent OAuth failures that need an operator action rather than a network retry.</summary>
public enum YouTubeAuthFailureKind
{
    MissingClientSecret,
    TokenRejected
}
