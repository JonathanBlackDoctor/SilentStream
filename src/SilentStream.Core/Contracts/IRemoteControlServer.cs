namespace SilentStream.Core.Contracts;

/// <summary>How the embedded remote-control web server binds (확장계획서 §4.4, D8).</summary>
public enum RemoteBindMode
{
    /// <summary>Not started (default).</summary>
    Off,

    /// <summary>Bind 0.0.0.0:port for same-Wi-Fi phones; adds a Windows firewall inbound rule.</summary>
    Lan,

    /// <summary>Bind the Tailscale interface IP for use away from home (traffic encrypted).</summary>
    Tailscale,

    /// <summary>
    /// Bind 127.0.0.1 only; a bundled cloudflared tunnel dials out to Cloudflare and exposes the
    /// loopback server over HTTPS from anywhere — no router port-forwarding, no LAN/firewall exposure.
    /// </summary>
    Cloudflare
}

/// <summary>
/// Embedded ASP.NET Core (Kestrel) server hosting the mobile remote-control web UI + REST/WS
/// API inside the WPF process (확장계획서 §4.4). PIN pairing issues per-device tokens (D11).
/// </summary>
public interface IRemoteControlServer
{
    /// <summary>Starts the server in the given bind mode (no-op for <see cref="RemoteBindMode.Off"/>).</summary>
    Task StartAsync(RemoteBindMode mode, int port, CancellationToken ct);

    /// <summary>Stops the server and releases the port.</summary>
    Task StopAsync();
}
