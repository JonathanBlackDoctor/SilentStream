using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Remote;

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
    private readonly object _processGate = new();

    private Process? _process;
    private CancellationTokenSource? _sessionCts;
    private Task? _namedSupervisorTask;
    private TaskCompletionSource<string>? _quickUrlTcs;

    public CloudflaredManager(ILogService log) => _log = log;

    public bool IsRunning
    {
        get
        {
            lock (_processGate)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>The public URL phones use, once known (null while a token-only tunnel runs without a
    /// configured hostname, or before a quick tunnel has reported its URL).</summary>
    public string? PublicUrl { get; private set; }

    /// <summary>
    /// Starts cloudflared and returns the public phone URL when it is known. With a
    /// <paramref name="token"/> it runs the configured named tunnel (<c>tunnel run --token</c>) and
    /// returns <c>https://{hostname}</c> when a hostname is given (else null). With no token it runs
    /// a quick tunnel (<c>--url http://localhost:{port}</c>) and returns the *.trycloudflare.com URL
    /// parsed from stderr (throwing on timeout / early exit). <paramref name="protocol"/>
    /// ("http2"/"quic"/"auto"; blank = cloudflared default) selects the edge transport — http2
    /// (TCP/443) survives the UDP-blocked networks that broke QUIC in the 2nd field test.
    /// </summary>
    public async Task<string?> StartAsync(
        string? token, int localPort, string? hostname, string? protocol, CancellationToken ct)
    {
        lock (_processGate)
        {
            if (_sessionCts is not null)
            {
                throw new InvalidOperationException("cloudflared가 이미 실행 중입니다.");
            }

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var hasToken = !string.IsNullOrWhiteSpace(token);
        var args = CloudflaredArgs.Build(token, localPort, protocol);

        if (hasToken)
        {
            // A named tunnel has a stable hostname. Supervise its child process for the whole app
            // lifetime: managed networks are commonly unavailable for a while during Windows
            // logon, and cloudflared can also exit after a transient edge failure.
            PublicUrl = string.IsNullOrWhiteSpace(hostname) ? null : $"https://{hostname}";
            var firstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _namedSupervisorTask = SuperviseNamedTunnelAsync(args, firstAttempt, _sessionCts!.Token);
            await firstAttempt.Task.ConfigureAwait(false);
            return PublicUrl;
        }

        // Quick tunnels learn their random URL from stderr and cannot keep the same public address
        // after a restart, so retain their existing one-session behaviour.
        _quickUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = CreateProcess(args);
        process.Exited += OnQuickTunnelExited;
        SetCurrentProcess(process);
        StartProcess(process);

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
        CancellationTokenSource? session;
        Task? supervisor;
        Process? process;
        lock (_processGate)
        {
            session = _sessionCts;
            if (session is null)
            {
                return;
            }

            _sessionCts = null;
            supervisor = _namedSupervisorTask;
            _namedSupervisorTask = null;
            process = _process;
        }

        session.Cancel();
        try
        {
            if (supervisor is not null)
            {
                await supervisor.ConfigureAwait(false);
            }
            else if (process is not null)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already exited.
        }
        finally
        {
            session.Dispose();
            _quickUrlTcs = null;
            PublicUrl = null;
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
    }

    private async Task SuperviseNamedTunnelAsync(
        string args, TaskCompletionSource<bool> firstAttempt, CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            Process? process = null;
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                process = CreateProcess(args);
                SetCurrentProcess(process);
                StartProcess(process);
                firstAttempt.TrySetResult(true);

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var ranLongEnoughToResetBackoff = DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(5);
                consecutiveFailures = ranLongEnoughToResetBackoff ? 1 : consecutiveFailures + 1;
                _log.Warn($"cloudflared 터널이 종료되었습니다(exit={process.ExitCode}). 자동으로 재연결합니다.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                // The local controller server is already running. Do not fail app startup merely
                // because cloudflared or the network was unavailable on this first attempt.
                firstAttempt.TrySetResult(false);
                _log.Warn($"cloudflared 터널 시작 실패({ex.Message}). 자동으로 재시도합니다.");
            }
            finally
            {
                if (process is not null)
                {
                    await TerminateAndDisposeAsync(process).ConfigureAwait(false);
                    ClearCurrentProcess(process);
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            var delay = CloudflaredRestartPolicy.DelayAfter(consecutiveFailures);
            _log.Info($"Cloudflare 터널 재연결을 {delay.TotalSeconds:0}초 후 시도합니다.");
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        firstAttempt.TrySetCanceled(ct);
    }

    private Process CreateProcess(string args)
    {
        var process = new Process
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
        process.ErrorDataReceived += OnStderr;
        process.OutputDataReceived += OnStderr;
        return process;
    }

    private static void StartProcess(Process process)
    {
        if (!process.Start())
        {
            throw new InvalidOperationException("cloudflared 프로세스를 시작하지 못했습니다.");
        }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
    }

    private void OnQuickTunnelExited(object? sender, EventArgs e)
    {
        _log.Warn("cloudflared 터널이 종료되었습니다.");
        _quickUrlTcs?.TrySetException(
            new InvalidOperationException("cloudflared가 URL을 보고하기 전에 종료되었습니다."));
    }

    private void SetCurrentProcess(Process process)
    {
        lock (_processGate)
        {
            _process = process;
        }
    }

    private void ClearCurrentProcess(Process process)
    {
        lock (_processGate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }
    }

    private async Task StopProcessAsync(Process process)
    {
        await TerminateAndDisposeAsync(process).ConfigureAwait(false);
        ClearCurrentProcess(process);
    }

    private static async Task TerminateAndDisposeAsync(Process process)
    {
        try
        {
            // cloudflared has no graceful stdin-EOF stop; give it a moment, then kill the tree.
            if (!process.HasExited &&
                !await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started or already exited.
        }
        finally
        {
            process.Dispose();
        }
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
