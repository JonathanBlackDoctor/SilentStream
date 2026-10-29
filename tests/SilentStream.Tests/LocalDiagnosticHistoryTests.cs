using System.Text;
using System.Text.Json;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public sealed class LocalDiagnosticHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "silentstream-history-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static readonly DateTime Now = new(2026, 10, 29, 9, 0, 0);

    [Fact]
    public void Reads_incident_day_and_file_inventory_without_modifying_sources()
    {
        var logs = Folder("logs"); var recordings = Folder("recordings"); var clips = Folder("clips");
        var original = Path.Combine(recordings, "MediaCaptureHelper_REC_2026-10-27_0753.mp4");
        File.WriteAllBytes(original, new byte[1217]);
        File.WriteAllText(Path.Combine(recordings, "MediaCaptureHelper_REC_2026-10-22_0753.mp4"), "old");
        File.WriteAllText(Path.Combine(clips, "2026-10-27_6교시.mp4"), "cut");
        var log = Path.Combine(logs, "MediaCaptureHelper_2026-10-27.log");
        var body = "2026-10-27 13:53:00.000 [WARN] FFmpeg stopped\n2026-10-27 14:00:00.000 [INFO] 녹화 파일 삭제(공간): old.mp4\n";
        File.WriteAllText(log, body);
        var result = LocalDiagnosticHistory.Collect(recordings, logs, clips, Now);
        Assert.Equal("ready", result.Status);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Files, f => f.Kind == "recording" && f.Bytes == 1217);
        Assert.Equal(2, result.Logs.Single(l => l.Date == "2026-10-27").Lines.Count);
        Assert.Equal(body, File.ReadAllText(log)); Assert.Equal(1217, new FileInfo(original).Length);
    }

    [Theory]
    [InlineData(2026, 10, 30, true)]
    [InlineData(2026, 10, 31, true)]
    [InlineData(2026, 11, 2, true)]
    [InlineData(2026, 11, 3, false)]
    public void Later_reboot_keeps_incident_within_recording_retention(int year, int month, int day, bool included)
    {
        var logs = Folder("logs"); var recordings = Folder("recordings");
        File.WriteAllText(Path.Combine(logs, "MediaCaptureHelper_2026-10-27.log"), "[WARN] incident\n");
        File.WriteAllText(Path.Combine(recordings, "MediaCaptureHelper_REC_2026-10-27_0753.mp4"), "original");
        var result = LocalDiagnosticHistory.Collect(recordings, logs, Folder("clips"), new DateTime(year, month, day));
        Assert.Equal(7, result.Logs.Count);
        Assert.Equal(included, result.Logs.Any(l => l.Date == "2026-10-27" && l.Exists));
        Assert.Equal(included, result.Files.Any(f => f.Name.Contains("2026-10-27", StringComparison.Ordinal)));
    }

    [Fact]
    public void Seven_busy_days_stay_within_authenticated_status_budget()
    {
        var logs = Folder("logs");
        for (var day = 0; day < 7; day++)
        {
            var date = Now.AddDays(-day).ToString("yyyy-MM-dd");
            var lines = Enumerable.Range(0, 100).Select(i => $"[WARN] FFmpeg {i:D3} " + new string('녹', 1800));
            File.WriteAllLines(Path.Combine(logs, $"MediaCaptureHelper_{date}.log"), lines);
        }
        var result = LocalDiagnosticHistory.Collect(Folder("recordings"), logs, Folder("clips"), Now);
        Assert.True(result.Truncated);
        Assert.All(result.Logs, log => { Assert.True(log.Exists); Assert.NotEmpty(log.Lines); });
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 240_000);
    }

    [Fact]
    public void Missing_day_is_distinct_from_present_but_uninteresting_log()
    {
        var logs = Folder("logs"); File.WriteAllText(Path.Combine(logs, "MediaCaptureHelper_2026-10-28.log"), "[INFO] unrelated\n");
        var result = LocalDiagnosticHistory.Collect(Folder("recordings"), logs, Folder("clips"), Now);
        Assert.False(result.Logs.Single(l => l.Date == "2026-10-27").Exists);
        Assert.True(result.Logs.Single(l => l.Date == "2026-10-28").Exists);
        Assert.Empty(result.Logs.Single(l => l.Date == "2026-10-28").Lines);
    }

    [Theory]
    [InlineData("FFmpeg rtmp://ingest/live/PRIVATEKEY", "PRIVATEKEY")]
    [InlineData("Authorization: Bearer PRIVATEVALUE", "PRIVATEVALUE")]
    [InlineData("access_token=PRIVATEVALUE", "PRIVATEVALUE")]
    [InlineData("{\"refresh_token\":\"PRIVATEVALUE\"}", "PRIVATEVALUE")]
    [InlineData("https://example.com/ingest?key=PRIVATEVALUE", "PRIVATEVALUE")]
    [InlineData("C:\\Users\\PrivatePerson\\Videos\\recording.mp4", "PrivatePerson")]
    [InlineData("user.private@example.com", "user.private@example.com")]
    [InlineData("cloudflared tunnel run --token PRIVATEVALUE", "PRIVATEVALUE")]
    [InlineData("Cookie: PRIVATEVALUE", "PRIVATEVALUE")]
    [InlineData("https://user:PRIVATEVALUE@example.com/api", "PRIVATEVALUE")]
    public void Redacts_secrets_accounts_and_profile_paths(string line, string secret) =>
        Assert.DoesNotContain(secret, LocalDiagnosticHistory.Scrub(line));

    [Fact]
    public void History_is_bounded_and_keeps_first_and_last_operational_events()
    {
        var logs = Folder("logs"); var path = Path.Combine(logs, "MediaCaptureHelper_2026-10-27.log");
        File.WriteAllLines(path, Enumerable.Range(0, 2000).Select(i => $"13:53:{i:0000} [WARN] FFmpeg event {i} " + new string('x', 300)));
        var result = LocalDiagnosticHistory.Collect(Folder("recordings"), logs, Folder("clips"), Now);
        var day = result.Logs.Single(l => l.Date == "2026-10-27");
        Assert.True(day.Truncated);
        Assert.Contains(day.Lines, x => x.Contains("event 0 "));
        Assert.Contains(day.Lines, x => x.Contains("event 1999 "));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length < 240_000);
    }

    [Fact]
    public void Closed_originals_are_probed_but_clips_and_active_files_are_not()
    {
        var recordings = Folder("recordings"); var clips = Folder("clips");
        var old = Path.Combine(recordings, "MediaCaptureHelper_REC_2026-10-27_0753.mp4");
        File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(Path.Combine(recordings, "MediaCaptureHelper_REC_2026-10-29_0752.mp4"), "active");
        File.WriteAllText(Path.Combine(clips, "2026-10-27_6교시.mp4"), "clip");
        var calls = new List<string>();
        var result = LocalDiagnosticHistory.Collect(recordings, Folder("logs"), clips, Now, null,
            path => { calls.Add(path); return new("read", true, 21600); });
        Assert.Equal(old, Assert.Single(calls));
        Assert.Equal(21600, result.Files.Single(f => f.Name == Path.GetFileName(old)).Media!.DurationSeconds);
    }

    [Fact]
    public void System_events_share_the_total_transport_budget()
    {
        var events = Enumerable.Range(0, 1000).Select(i => new DiagnosticSystemEvent("System", i,
            DateTime.UtcNow, "provider", new string('한', 700))).ToArray();
        var result = LocalDiagnosticHistory.Collect(Folder("recordings"), Folder("logs"), Folder("clips"), Now,
            () => new("ready", events, false));
        Assert.True(result.Truncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 240_000);
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("silentstream-history-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup target.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
