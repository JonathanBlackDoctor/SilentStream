using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Remote;

/// <summary>Bounded, read-only history carried by the existing authenticated status connection.</summary>
public sealed class LocalDiagnosticHistory(IConfigStore config, string logsDirectory, string clipsDirectory,
    Func<DiagnosticSystemEvents>? readSystemEvents = null,
    Func<string, DiagnosticMediaProbeResult>? probeMedia = null)
{
    // Match the default recording retention so a later reboot does not hide the incident day.
    public const int HistoryDays = 7;

    private readonly object _gate = new();
    private DiagnosticHistorySnapshot _snapshot = new("collecting", null, [], [], false);
    private bool _collecting;
    private DateTime _lastAttemptUtc;

    public DiagnosticHistorySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_collecting && DateTime.UtcNow - _lastAttemptUtc >= TimeSpan.FromMinutes(2))
            {
                _collecting = true;
                _lastAttemptUtc = DateTime.UtcNow;
                _ = Task.Run(() =>
                {
                    DiagnosticHistorySnapshot next;
                    try { next = Collect(config.Load().Recording.Folder, logsDirectory, clipsDirectory, DateTime.Now,
                        readSystemEvents, probeMedia); }
                    catch (Exception)
                    { next = new("unavailable", DateTimeOffset.UtcNow, [], [], true); }
                    lock (_gate) { _snapshot = next; _collecting = false; }
                });
            }
            return _snapshot;
        }
    }

    public static DiagnosticHistorySnapshot Collect(string recordingsDirectory, string logsDirectory,
        string clipsDirectory, DateTime nowLocal, Func<DiagnosticSystemEvents>? readSystemEvents = null,
        Func<string, DiagnosticMediaProbeResult>? probeMedia = null)
    {
        var timer = Stopwatch.StartNew();
        var days = Enumerable.Range(0, HistoryDays).Select(i => DateOnly.FromDateTime(nowLocal).AddDays(-i)).ToArray();
        var dates = days.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var logs = days.Select(day => ReadDay(logsDirectory, day, timer)).ToArray();
        var files = new List<DiagnosticMediaFile>();
        var limited = logs.Any(l => l.Truncated);
        foreach (var (folder, kind) in new[] { (recordingsDirectory, "recording"), (clipsDirectory, "clip") })
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var examined = 0;
                foreach (var path in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly))
                {
                    if (++examined > 4096 || files.Count >= 200 || timer.Elapsed > TimeSpan.FromSeconds(10))
                    { limited = true; break; }
                    var name = Path.GetFileName(path);
                    if (!dates.Any(date => name.Contains(date, StringComparison.Ordinal))) continue;
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    files.Add(new(kind, name, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { limited = true; }
        }
        var result = new DiagnosticHistorySnapshot("ready", DateTimeOffset.UtcNow, logs,
            files.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Kind).ToArray(), limited,
            readSystemEvents?.Invoke());
        if (probeMedia is not null)
        {
            result = result with { Files = result.Files.Select(file =>
            {
                if (file.Kind != "recording" || timer.Elapsed > TimeSpan.FromSeconds(12)
                    || DateTime.UtcNow - file.LastWriteUtc < TimeSpan.FromSeconds(30)) return file;
                return file with { Media = probeMedia(Path.Combine(recordingsDirectory, file.Name)) };
            }).ToArray() };
        }
        // Leave ample headroom for the pre-existing queue, playlist and inspection status fields.
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > 240_000)
            result = result with
            {
                Logs = result.Logs.Select(l => l with
                {
                    Lines = l.Lines.Take(l.Lines.Count / 4).Concat(l.Lines.TakeLast(l.Lines.Count / 4)).ToArray(),
                    Truncated = true
                }).ToArray(),
                Files = result.Files.Take(Math.Max(1, result.Files.Count / 2)).ToArray(),
                SystemEvents = result.SystemEvents is { } system ? system with
                { Items = system.Items.Take(system.Items.Count / 2).ToArray(), Truncated = true } : null,
                Truncated = true
            };
        return result;
    }

    private static DiagnosticLogDay ReadDay(string directory, DateOnly day, Stopwatch timer)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"MediaCaptureHelper_{date}.log");
        if (!File.Exists(path)) return new(date, false, null, null, [], false);
        try
        {
            var info = new FileInfo(path);
            var operational = new List<(int Index, string Text)>();
            var upload = new List<(int Index, string Text)>();
            var truncated = false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // Cap the bytes before decoding: even a malformed log with no newline is bounded.
            var input = new byte[(int)Math.Min(file.Length, 8_000_000)];
            var read = 0;
            while (read < input.Length)
            {
                var count = file.Read(input, read, input.Length - read);
                if (count == 0) break;
                read += count;
            }
            truncated |= file.Length > read;
            using var reader = new StringReader(Encoding.UTF8.GetString(input, 0, read));
            var index = 0;
            var scannedBytes = 0;
            while (reader.ReadLine() is { } line)
            {
                index++;
                scannedBytes += Encoding.UTF8.GetByteCount(line) + 1;
                if (scannedBytes > 8_000_000 || timer.Elapsed > TimeSpan.FromSeconds(8))
                { truncated = true; break; }
                if (line.Length > 4096) line = line[..4096];
                if (!Interesting(line)) continue;
                var target = line.Contains("업로드", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("quota", StringComparison.OrdinalIgnoreCase) ? upload : operational;
                target.Add((index, Scrub(line)));
                if (target.Count > 384) { target.RemoveAt(192); truncated = true; }
            }
            var selected = Ends(operational, 70).Concat(Ends(upload, 16))
                .DistinctBy(x => x.Index).OrderBy(x => x.Index).ToArray();
            var lines = new List<string>();
            var bytes = 0;
            foreach (var item in selected)
            {
                var size = JsonEncodedText.Encode(item.Text).EncodedUtf8Bytes.Length + 4;
                if (bytes + size > 48_000) { truncated = true; break; }
                lines.Add(item.Text); bytes += size;
            }
            truncated |= selected.Length < operational.Count + upload.Count;
            return new(date, true, info.Length, info.LastWriteTimeUtc, lines, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(date, true, null, null, [], true); }
    }

    private static IEnumerable<(int Index, string Text)> Ends(List<(int Index, string Text)> source, int count) =>
        source.Count <= count ? source : source.Take(count / 2).Concat(source.TakeLast(count - count / 2));

    private static bool Interesting(string line) => new[]
    {
        "[WARN]", "[ERROR]", "[FATAL]", "FFmpeg", "RTMP", "워치독", "인코더", "녹화", "캡처",
        "시작 완료", "종료", "업데이트", "무손실 컷", "세션", "pipeline", "encoder", "capture"
    }.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static string Scrub(string line)
    {
        line = Regex.Replace(line, @"\brtmps?://\S+", "[stream-url]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\b(Bearer\s+)\S+", "$1[redacted]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, "(?i)([\\\"']?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|authorization|password|pin|stream[_-]?key|token|cookie|set-cookie|cloudflareTunnelToken(?:Enc)?|appApiToken(?:MachineEnc)?)[\\\"']?(?:\\s*[:=]\\s*|\\s+)[\\\"']?)[^\\s,;\\\"']+", "$1[redacted]");
        line = Regex.Replace(line, @"https?://[^\s\""<>]+", match =>
        {
            return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? $"[url:{uri.Host}]" : "[url]";
        }, RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[email]");
        line = Regex.Replace(line, @"[A-Za-z]:[\\/]Users[\\/][^\\/\s\""']+", "%USERPROFILE%", RegexOptions.IgnoreCase);
        return line;
    }
}

public sealed record DiagnosticHistorySnapshot(string Status, DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<DiagnosticLogDay> Logs, IReadOnlyList<DiagnosticMediaFile> Files, bool Truncated,
    DiagnosticSystemEvents? SystemEvents = null);
public sealed record DiagnosticLogDay(string Date, bool Exists, long? FileBytes,
    DateTime? LastWriteUtc, IReadOnlyList<string> Lines, bool Truncated);
public sealed record DiagnosticMediaFile(string Kind, string Name, long Bytes, DateTime CreatedUtc, DateTime LastWriteUtc,
    DiagnosticMediaProbeResult? Media = null);
public sealed record DiagnosticMediaProbeResult(string Status, bool? HasMediaSamples, double? DurationSeconds);
public sealed record DiagnosticSystemEvents(string Status, IReadOnlyList<DiagnosticSystemEvent> Items, bool Truncated);
public sealed record DiagnosticSystemEvent(string Log, int Id, DateTime? TimeUtc, string Provider, string Details);
