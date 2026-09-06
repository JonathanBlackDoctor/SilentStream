using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace SilentStream.Inspection.Coordinator;

public sealed class CloudflaredHostedService : BackgroundService
{
    private readonly CoordinatorOptionsStore _options;
    private readonly ILogger<CloudflaredHostedService> _logger;
    private Process? _process;

    public CloudflaredHostedService(CoordinatorOptionsStore options, ILogger<CloudflaredHostedService> logger)
    {
        _options = options; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.Current.CentralControlEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                continue;
            }
            var token = _options.UnprotectMachine(_options.Current.CloudflareTunnelTokenMachineEnc);
            if (string.IsNullOrWhiteSpace(token))
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                continue;
            }
            var executable = _options.Current.CloudflaredPath;
            if (string.IsNullOrWhiteSpace(executable)) executable = Path.Combine(AppContext.BaseDirectory, "cloudflared", "cloudflared.exe");
            if (!File.Exists(executable))
            {
                _logger.LogWarning("cloudflared executable was not found at {Path}.", executable);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                continue;
            }
            var protocol = _options.Current.CloudflareProtocol is "quic" or "auto" ? _options.Current.CloudflareProtocol : "http2";
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("tunnel"); start.ArgumentList.Add("--no-autoupdate");
            start.ArgumentList.Add("--protocol"); start.ArgumentList.Add(protocol);
            start.Environment["TUNNEL_TOKEN"] = token;
            start.ArgumentList.Add("run");
            try
            {
                _process = Process.Start(start);
                if (_process is null) throw new InvalidOperationException("cloudflared did not start.");
                _ = DrainAsync(_process.StandardOutput, stoppingToken);
                _ = DrainAsync(_process.StandardError, stoppingToken);
                _logger.LogInformation("Named Cloudflare tunnel started for {Hostname}.", _options.Current.CloudflareHostname);
                await _process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
                if (!stoppingToken.IsCancellationRequested)
                    _logger.LogWarning("cloudflared exited unexpectedly with code {ExitCode}; reconnecting.", _process.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "Cloudflare tunnel stopped unexpectedly."); }
            finally
            {
                try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
                _process?.Dispose(); _process = null;
            }
            if (!stoppingToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) break;
            if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase))
                _logger.LogInformation("cloudflared connected: {Message}", line);
            else if (line.Contains("ERR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("cloudflared: {Message}", line);
        }
    }
}
