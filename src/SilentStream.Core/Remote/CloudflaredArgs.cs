namespace SilentStream.Core.Remote;

/// <summary>
/// Builds the cloudflared command-line arguments for the remote-control tunnel. Kept in Core as a
/// pure function so the edge-transport selection — the fix for the 2nd field test's QUIC-blocked
/// network failure ("failed to dial to edge with quic", no automatic fallback) — is unit-tested
/// without spawning the Windows child process. The App-layer CloudflaredManager owns the process
/// plumbing; this owns only the argument string.
/// </summary>
public static class CloudflaredArgs
{
    /// <summary>
    /// Named tunnel (<paramref name="token"/> present):
    /// <c>tunnel --no-autoupdate [--protocol X] run --token …</c>.
    /// Quick tunnel (no token):
    /// <c>tunnel --no-autoupdate [--protocol X] --url http://localhost:{localPort}</c>.
    /// <paramref name="protocol"/> ("http2"/"quic"/"auto") becomes a <c>--protocol</c> flag bound to
    /// the <c>tunnel</c> command; blank omits it so cloudflared uses its own default.
    /// </summary>
    public static string Build(string? token, int localPort, string? protocol)
    {
        var proto = string.IsNullOrWhiteSpace(protocol) ? string.Empty : $"--protocol {protocol.Trim()} ";
        var hasToken = !string.IsNullOrWhiteSpace(token);
        return hasToken
            ? $"tunnel --no-autoupdate {proto}run --token {token}"
            : $"tunnel --no-autoupdate {proto}--url http://localhost:{localPort}";
    }
}
