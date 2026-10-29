using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using SilentStream.Core.Remote;

namespace SilentStream.Platform.Windows;

/// <summary>Recent application crashes, display/storage resets and power events; never changes event logs.</summary>
public static class WindowsDiagnosticHistory
{
    public static DiagnosticSystemEvents Read()
    {
        var events = new List<DiagnosticSystemEvent>();
        var truncated = false;
        var accessible = true;
        var timer = Stopwatch.StartNew();
        var start = DateTime.Today.AddDays(-2).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        foreach (var (log, ids) in new[]
        {
            ("Application", "EventID=1000 or EventID=1001 or EventID=1002 or EventID=1026"),
            ("System", "EventID=41 or EventID=6008 or EventID=6005 or EventID=6006 or EventID=1074 or EventID=4101 or EventID=7 or EventID=51 or EventID=55 or EventID=129 or EventID=153 or EventID=157")
        })
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName,
                    $"*[System[({ids}) and TimeCreated[@SystemTime >= '{start}']]]");
                using var reader = new EventLogReader(query);
                for (var i = 0; i < 64; i++)
                {
                    if (timer.Elapsed > TimeSpan.FromSeconds(3)) { truncated = true; break; }
                    using var item = reader.ReadEvent();
                    if (item is null) break;
                    var description = string.Join(" | ", item.Properties.Take(12).Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
                    if (log == "Application" && !new[] { "MediaCaptureHelper", "SilentStream", "ffmpeg", "cloudflared" }
                        .Any(name => description.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;
                    description = LocalDiagnosticHistory.Scrub(description);
                    if (description.Length > 700) description = description[..700];
                    events.Add(new(log, item.Id, item.TimeCreated?.ToUniversalTime(), item.ProviderName ?? "", description));
                    if (i == 63) truncated = true;
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
            { accessible = false; }
        }
        return new(accessible ? "ready" : "partial-or-denied", events, truncated);
    }
}
