using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;

namespace SilentStream.App.Remote;

/// <summary>
/// Supervises the bundled <c>cloudflared</c> child process that exposes the loopback remote-control
/// server (127.0.0.1:port) to the internet over HTTPS — no router port-forwarding and no inbound
/// firewall rule, because the tunnel dials outbound to the Cloudflare edge. Mirrors the
/// EncoderPipeline child-process idiom: spawn with stderr redirected, stream it to the log, and
/// kill the whole process tree on stop.
/// </summary>
public sealed class CloudflaredManager : IDisposable
{
    private static readonly Regex QuickTunnelUrl =
        new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogService _log;

    private Process? _process;
    private CancellationTokenSource? _sessionCts;
    private TaskCompletionSource<string>? _quickUrlTcs;

    public CloudflaredManager(ILogService log) => _log = log;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The public URL phones use, once known (null while a token-only tunnel runs without a
    /// configured hostname, or before a quick tunnel has reported its URL).</summary>
    public string? PublicUrl { get; private set; }

    /// <summary>
    /// Starts cloudflared and returns the public phone URL when it is known. With a
    /// <paramref name="token"/> it runs the configured named tunnel (<c>tunnel run --token</c>) and
    /// returns <c>https://{hostname}</c> when a hostname is given (else null). With no token it runs
    /// a quick tunnel (<c>--url http://localhost:{port}</c>) and returns the *.trycloudflare.com URL
    /// parsed from stderr (throwing on timeout / early exit).
    /// </summary>
    public async Task<string?> StartAsync(string? token, int localPort, string? hostname, CancellationToken ct)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("cloudflared가 이미 실행 중입니다.");
        }

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        var args = hasToken
            ? $"tunnel --no-autoupdate run --token {token}"
            : $"tunnel --no-autoupdate --url http://localhost:{localPort}";

        // Only quick tunnels learn their URL from stderr; a named tunnel's URL is its hostname.
        _quickUrlTcs = hasToken
            ? null
            : new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveCloudflared(),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };
        _process.ErrorDataReceived += OnStderr;
        _process.OutputDataReceived += OnStderr;
        _process.Exited += (_, _) =>
        {
            _log.Warn("cloudflared 터널이 종료되었습니다.");
            _quickUrlTcs?.TrySetException(
                new InvalidOperationException("cloudflared가 URL을 보고하기 전에 종료되었습니다."));
        };

        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        if (hasToken)
        {
            // Named tunnel: the public URL is whatever hostname the user mapped in the dashboard.
            PublicUrl = string.IsNullOrWhiteSpace(hostname) ? null : $"https://{hostname}";
            return PublicUrl;
        }

        // Quick tunnel: the random *.trycloudflare.com URL only appears in stderr — wait for it.
        var tcs = _quickUrlTcs!;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeoutCts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException("cloudflared가 제한 시간 내 URL을 보고하지 않았습니다."))))
        {
            PublicUrl = await tcs.Task.ConfigureAwait(false);
            return PublicUrl;
        }
    }

    public async Task StopAsync()
    {
        _sessionCts?.Cancel();
        var process = _process;
        if (process is null)
        {
            return;
        }
        try
        {
            // cloudflared has no graceful stdin-EOF stop; give it a moment, then kill the tree.
            if (!process.HasExited &&
                !await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        finally
        {
            process.Dispose();
            _process = null;
            _log.Info("Cloudflare 터널을 정지했습니다.");
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort cleanup on dispose.
        }
        _sessionCts?.Dispose();
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }
        var match = QuickTunnelUrl.Match(e.Data);
        if (match.Success)
        {
            _quickUrlTcs?.TrySetResult(match.Value);
            return;
        }
        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"cloudflared: {e.Data}");
        }
    }

    /// <summary>cloudflared\cloudflared.exe next to the app (Velopack bundle), else "cloudflared" on PATH.</summary>
    private static string ResolveCloudflared()
    {
        var exeName = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var bundled = Path.Combine(AppContext.BaseDirectory, "cloudflared", exeName);
        return File.Exists(bundled) ? bundled : "cloudflared";
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
