using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SilentStream.Core.Remote;

namespace SilentStream.Core.Media;

/// <summary>Reads a closed recording's metadata; no output media is created.</summary>
public static class DiagnosticMediaProbe
{
    public static DiagnosticMediaProbeResult Read(string path) => Read(path, FfmpegLocator.Resolve());

    public static DiagnosticMediaProbeResult Read(string path, string ffmpegPath)
    {
        try
        {
            var hasSamples = Mp4MediaSamples.HasSamples(path);
            if (!hasSamples && new FileInfo(path).Length < 2048) return new("no-media-samples", false, null);
            var start = new ProcessStartInfo(ffmpegPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new("probe-unavailable", hasSamples, null);
            var output = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            { process.Kill(entireProcessTree: true); return new("probe-timeout", hasSamples, null); }
            var match = Regex.Match(output.GetAwaiter().GetResult(), @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            var duration = match.Success
                ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
                : (double?)null;
            return new("read", hasSamples, duration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new("probe-unavailable", null, null); }
    }
}
