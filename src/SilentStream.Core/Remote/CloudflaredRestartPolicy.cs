namespace SilentStream.Core.Remote;

/// <summary>
/// Backoff used when a named Cloudflare tunnel process exits. Classroom PCs often start before
/// their managed network is ready, so a one-shot child process leaves the public controller
/// offline until somebody restarts the app. The bounded delay keeps retrying unattended without
/// creating a tight loop for a persistently invalid token or blocked network.
/// </summary>
public static class CloudflaredRestartPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1)
    ];

    public static TimeSpan DelayAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        return Delays[Math.Min(consecutiveFailures, Delays.Length) - 1];
    }
}
