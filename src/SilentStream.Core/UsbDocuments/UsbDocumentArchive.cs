using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.UsbDocuments;

/// <summary>User-context, bounded, single-reader archive. Detector never reads file contents.</summary>
public sealed class UsbDocumentArchive : IDisposable
{
    public const int ChunkSize = 256 * 1024;
    public const int RetentionDays = 7;
    private readonly IConfigStore _config;
    private readonly string _catalog;
    private readonly object _gate = new();
    private readonly List<UsbDocumentEntry> _entries;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(128);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Task _retentionWorker;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, int> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _chunkReads = new();
    private readonly long _bytesPerSecond;
    private readonly Func<string, long> _freeSpace;
    private string _detector = "관리자 서비스 연결 대기";
    private DateTimeOffset _heartbeat;
    private long _dropped;
    private string? _storageError;

    public UsbDocumentArchive(IConfigStore config, string catalog, long bytesPerSecond = 4 * 1024 * 1024, Func<string, long>? freeSpace = null,
        Func<DateTimeOffset>? utcNow = null, bool startRetentionWorker = true)
    {
        _config = config; _catalog = catalog; _bytesPerSecond = Math.Max(1, bytesPerSecond);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _freeSpace = freeSpace ?? (directory => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace);
        try
        {
            _entries = File.Exists(catalog)
                ? JsonSerializer.Deserialize<List<UsbDocumentEntry>>(File.ReadAllText(catalog)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preserve unreadable state, never overwrite it with an empty catalogue.
            _entries = []; _storageError = "보관 목록을 읽을 수 없습니다. PC에서 목록 파일을 확인하세요.";
        }
        foreach (var entry in _entries.Where(x => x.Status is "queued" or "copying"))
        { entry.Status = "failed"; entry.Error = "앱 종료로 복사가 중단됐습니다. 재시도하세요."; CleanupTemporary(entry); }
        if (_entries.Count > 0 && _storageError is null) Save();
        _worker = Task.Run(RunAsync);
        _retentionWorker = startRetentionWorker ? Task.Run(RetentionLoopAsync) : Task.CompletedTask;
    }

    public UsbDocumentConfig Settings => _config.Load().UsbDocuments;
    public void Configure(bool enabled, string? folder = null)
    {
        if (folder is not null)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                throw new ArgumentException("절대 경로로 저장 폴더를 선택하세요.");
            folder = Path.GetFullPath(folder.Trim());
            if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\") || folder.Length > 220)
                throw new ArgumentException("이 PC의 로컬 저장 폴더를 선택하세요.");
            EnsureNoReparsePoints(folder);
        }
        _config.Update(c => { c.UsbDocuments.Enabled = enabled; if (folder is not null) c.UsbDocuments.Folder = folder; });
    }

    public UsbDocumentSnapshot Snapshot(string? date = null, int skip = 0)
    {
        lock (_gate)
        {
            var filtered = _entries.Where(e => date is null || e.Date == date).OrderByDescending(e => e.DetectedAtUtc).ToArray();
            var enabled = Settings.Enabled;
            var detector = _storageError ?? (!enabled ? "꺼짐" :
                DateTimeOffset.UtcNow - _heartbeat > TimeSpan.FromSeconds(10) ? "관리자 서비스 연결 끊김" : _detector);
            return new(true, enabled, detector, _dropped,
                filtered.Skip(Math.Max(0, skip)).Take(100).Select(e => e.Public()).ToArray(), filtered.Length);
        }
    }

    public void Heartbeat(string status, long dropped)
    {
        lock (_gate) { _heartbeat = DateTimeOffset.UtcNow; _detector = status; _dropped = dropped; }
    }

    public static bool IsDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ppt" or ".pptx" or ".pdf";

    public void Observe(UsbReadNotice notice)
    {
        if (!Settings.Enabled || !IsDocument(notice.Path) || !Path.IsPathFullyQualified(notice.Path)) return;
        lock (_gate)
        {
            if (_storageError is not null) return;
            string fingerprint; long length = 0;
            try
            {
                var source = new FileInfo(notice.Path);
                length = source.Length;
                fingerprint = notice.ConnectionId + "|" + notice.Path.ToUpperInvariant() + "|" + length + "|" + source.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { fingerprint = notice.ConnectionId + "|" + notice.Path.ToUpperInvariant() + "|unavailable"; }
            if (_entries.Any(e => e.Fingerprint == fingerprint || e.Source.Equals(notice.Path, StringComparison.OrdinalIgnoreCase)
                && e.Status is "queued" or "copying")) return;
            var entry = new UsbDocumentEntry
            {
                Source = notice.Path, Fingerprint = fingerprint, Name = Path.GetFileName(notice.Path),
                Size = length, DetectedAtUtc = notice.ReadAtUtc,
                Date = notice.ReadAtUtc.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd")
            };
            _entries.Add(entry);
            if (!_queue.Writer.TryWrite(entry.Id))
            { entry.Status = "failed"; entry.Error = "복사 대기열이 가득 찼습니다. 재시도하세요."; }
            Save();
        }
    }

    public bool Retry(string id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id && e.Status == "failed");
            if (!Settings.Enabled || entry is null || _storageError is not null || !_queue.Writer.TryWrite(id)) return false;
            entry.Status = "queued"; entry.Error = null; entry.CopiedBytes = 0; Save(); return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                UsbDocumentEntry entry;
                lock (_gate) entry = _entries.Single(e => e.Id == id);
                try { await CopyAsync(entry, _stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
                {
                    lock (_gate)
                    {
                        entry.Status = "failed";
                        entry.Error = ex switch
                        {
                            OperationCanceledException => "복사가 중단됐습니다. 재시도하세요.",
                            UnauthorizedAccessException => "파일 또는 저장 폴더에 접근할 수 없습니다.",
                            _ => "원본 분리·변경, 파일 잠금 또는 저장 공간을 확인하고 재시도하세요."
                        };
                        Save();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CopyAsync(UsbDocumentEntry entry, CancellationToken token)
    {
        if (!Settings.Enabled) throw new OperationCanceledException();
        EnsureNoReparsePoints(entry.Source);
        var before = new FileInfo(entry.Source);
        var length = before.Length; var modified = before.LastWriteTimeUtc;
        var root = Path.GetFullPath(Settings.Folder);
        var directory = Path.Combine(root, entry.Date);
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        if (_freeSpace(directory) < length + 1024L * 1024 * 1024) throw new IOException("Insufficient space.");
        CleanupTemporary(entry);
        var temporary = Path.Combine(directory, "." + entry.Id + ".partial");
        lock (_gate) { entry.TemporaryPath = temporary; Save(); }
        try
        {
            // No write-sharing: a document changing during the copy must not be certified.
            await using (var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                lock (_gate) { entry.Status = "copying"; entry.Size = length; entry.CopiedBytes = 0; Save(); }
                var buffer = new byte[ChunkSize]; var clock = Stopwatch.StartNew(); long copied = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Settings.Enabled) throw new OperationCanceledException();
                    var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count); copied += count;
                    lock (_gate) entry.CopiedBytes = copied;
                    var delay = TimeSpan.FromSeconds((double)copied / _bytesPerSecond) - clock.Elapsed;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                before.Refresh();
                // A move may unlink the source after its handle was opened. Its bytes remain readable.
                if (copied != length || input.Length != length || before.Exists && before.LastWriteTimeUtc != modified)
                    throw new IOException("Source changed.");
                entry.Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            lock (_gate)
            {
                var duplicate = _entries.FirstOrDefault(e => e.Id != entry.Id && e.Status == "ready"
                    && e.Sha256 == entry.Sha256 && File.Exists(e.Destination));
                if (duplicate is not null)
                { File.Delete(temporary); entry.Destination = duplicate.Destination; entry.Status = "duplicate"; }
                else
                {
                    // Unique ID prevents overwrites even if names, dates and hash prefixes collide.
                    entry.Destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(entry.Name)
                        + "_" + entry.Id + Path.GetExtension(entry.Name));
                    File.Move(temporary, entry.Destination);
                    entry.Status = "ready";
                }
                entry.DestinationWriteTimeUtc = File.GetLastWriteTimeUtc(entry.Destination);
                entry.ArchivedAtUtc = _utcNow();
                entry.Error = null; Save();
            }
        }
        finally { CleanupTemporary(entry); }
    }

    public (FileStream Stream, UsbDocumentInfo Info)? OpenDownload(string id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id && e.Status is "ready" or "duplicate");
            if (entry is null || !Guid.TryParseExact(id, "N", out _)) return null;
            EnsureNoReparsePoints(entry.Destination);
            var path = Path.GetFullPath(entry.Destination);
            _readers[path] = _readers.GetValueOrDefault(path) + 1;
            ArchiveReadStream? stream = null;
            try
            {
                stream = new ArchiveReadStream(path, () => ReleaseReader(path));
                if (stream.Length != entry.Size || File.GetLastWriteTimeUtc(path) != entry.DestinationWriteTimeUtc)
                    throw new IOException("Archived file changed.");
                return (stream, entry.Public());
            }
            catch
            {
                if (stream is null) ReleaseReader(path); else stream.Dispose();
                throw;
            }
        }
    }

    public async Task<UsbDocumentChunk?> ReadChunkAsync(string id, long offset, CancellationToken token)
    {
        var opened = OpenDownload(id);
        if (opened is null) return null;
        // Central downloads open each chunk separately. Keep the file between chunks, too.
        lock (_gate) _chunkReads[id] = _utcNow();
        await using var stream = opened.Value.Stream;
        if (offset < 0 || offset > stream.Length || offset % ChunkSize != 0) throw new ArgumentException("Invalid offset.");
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(ChunkSize, stream.Length - offset)];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        var info = opened.Value.Info;
        return new(id, info.Name, info.Size, offset, info.Sha256!, Convert.ToBase64String(buffer), stream.Position == stream.Length);
    }

    private async Task RetentionLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pruned = PruneExpired();
                await Task.Delay(pruned == 128 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(15), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Remove only catalog-owned, unchanged files under the current archive root. Never scans a tree.</summary>
    public int PruneExpired()
    {
        lock (_gate)
        {
            if (_storageError is not null) return 0;
            var now = _utcNow(); var cutoff = now.AddDays(-RetentionDays); var pruned = 0;
            var groups = _entries.Where(e => e.Status is "ready" or "duplicate")
                .GroupBy(e => e.Destination, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var group in groups)
            {
                if (_stop.IsCancellationRequested || pruned == 128) break;
                if (group.Any(e => (e.ArchivedAtUtc ?? e.DetectedAtUtc) == default
                    || (e.ArchivedAtUtc ?? e.DetectedAtUtc) > cutoff
                    || _chunkReads.TryGetValue(e.Id, out var read) && now - read < TimeSpan.FromMinutes(2))) continue;
                var owner = group.FirstOrDefault(e => e.Status == "ready");
                if (owner is null || !ManagedDestination(owner, out var path) || _readers.GetValueOrDefault(path) > 0) continue;
                try
                {
                    EnsureNoReparsePoints(path);
                    var file = new FileInfo(path);
                    if (file.Exists)
                    {
                        if (file.Length != owner.Size || file.LastWriteTimeUtc != owner.DestinationWriteTimeUtc) continue;
                        // Path was checked against the exact archive root/date/generated-name before deletion.
                        File.Delete(path);
                    }
                    foreach (var entry in group) { _entries.Remove(entry); _chunkReads.Remove(entry.Id); }
                    Save(); pruned++;
                    if (_storageError is not null) break;
                    var directory = Path.GetDirectoryName(path)!;
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* An external reader/lock can defer this file until the next bounded pass. */ }
            }
            return pruned;
        }
    }

    private bool ManagedDestination(UsbDocumentEntry entry, out string path)
    {
        path = "";
        try
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.Name != Path.GetFileName(entry.Name)
                || !IsDocument(entry.Name) || !DateTime.TryParseExact(entry.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || !Path.IsPathFullyQualified(Settings.Folder) || !Path.IsPathFullyQualified(entry.Destination)) return false;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Settings.Folder));
            path = Path.GetFullPath(entry.Destination);
            var expected = Path.Combine(root, entry.Date,
                Path.GetFileNameWithoutExtension(entry.Name) + "_" + entry.Id + Path.GetExtension(entry.Name));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return !root.StartsWith(@"\\") && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && path.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private void ReleaseReader(string path)
    {
        lock (_gate)
        {
            var count = _readers.GetValueOrDefault(path);
            if (count <= 1) _readers.Remove(path); else _readers[path] = count - 1;
        }
    }

    private sealed class ArchiveReadStream(string path, Action release) : FileStream(path,
        FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        private Action? _release = release;
        protected override void Dispose(bool disposing)
        { try { base.Dispose(disposing); } finally { Interlocked.Exchange(ref _release, null)?.Invoke(); } }
        public override async ValueTask DisposeAsync()
        { try { await base.DisposeAsync().ConfigureAwait(false); } finally { Interlocked.Exchange(ref _release, null)?.Invoke(); } }
    }

    private static void CleanupTemporary(UsbDocumentEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TemporaryPath)
            || Path.GetFileName(entry.TemporaryPath) != "." + entry.Id + ".partial") return;
        try
        {
            EnsureNoReparsePoints(entry.TemporaryPath);
            if (File.Exists(entry.TemporaryPath)) File.Delete(entry.TemporaryPath);
            entry.TemporaryPath = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse paths are not supported.");
    }

    private void Save()
    {
        if (_storageError is not null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_catalog)!);
            File.WriteAllText(_catalog + ".tmp", JsonSerializer.Serialize(_entries));
            File.Move(_catalog + ".tmp", _catalog, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _storageError = "보관 목록 저장 실패. PC의 저장 공간과 권한을 확인하세요."; }
    }

    public void Dispose()
    {
        _stop.Cancel(); _queue.Writer.TryComplete();
        try { Task.WhenAll(_worker, _retentionWorker).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
