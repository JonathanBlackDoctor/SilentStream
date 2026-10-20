using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.UsbDocuments;

public sealed class DocumentDriveConfig
{
    public bool Enabled { get; set; }
    public string Account { get; set; } = "";
    public string FolderId { get; set; } = "";
}

public interface IDocumentDriveClient
{
    Task ConnectAsync(DocumentDriveConfig settings, CancellationToken ct);
    Task ValidateAsync(DocumentDriveConfig settings, CancellationToken ct);
    Task<string> AllocateIdAsync(CancellationToken ct);
    Task UploadAsync(DocumentDriveConfig settings, UsbDocumentInfo file, Stream content,
        string remoteId, string md5, CancellationToken ct);
}

public sealed class DocumentDriveUploadRecord
{
    public string Key { get; set; } = "";
    public string RemoteId { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
}

/// <summary>Deterministic, bounded worker. Failed content is attempted again only on explicit retry.</summary>
public sealed class DocumentDriveUpload : IDisposable
{
    private readonly UsbDocumentArchive _archive;
    private readonly IConfigStore _config;
    private readonly IDocumentDriveClient _client;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _run = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<DocumentDriveUploadRecord> _records;
    private readonly Task _worker;
    private bool _stateBroken;
    private string _status = "Drive 자동 업로드 꺼짐";
    private int _cursor;
    public string Status { get { lock (_gate) return _status; } }
    public DocumentDriveConfig Settings => _config.Load().UsbDocuments.Drive;

    public DocumentDriveUpload(UsbDocumentArchive archive, IConfigStore config, IDocumentDriveClient client,
        string stateFile, bool startWorker = true)
    {
        _archive = archive; _config = config; _client = client; _stateFile = stateFile;
        try
        {
            _records = File.Exists(stateFile)
                ? JsonSerializer.Deserialize<List<DocumentDriveUploadRecord>>(File.ReadAllText(stateFile))
                    ?? throw new JsonException() : [];
            foreach (var record in _records.Where(x => x.Status == "uploading"))
            { record.Status = "failed"; record.Error = "앱 종료로 중단됐습니다. Drive 업로드 재시도를 누르세요."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _records = []; _stateBroken = true; _status = "Drive 업로드 기록을 읽을 수 없습니다. 원본 기록을 확인하세요."; }
        _worker = startWorker ? Task.Run(WorkAsync) : Task.CompletedTask;
    }

    public static string FolderId(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "drive.google.com") throw new ArgumentException("Drive 폴더 링크를 입력하세요.");
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(parts, "folders");
            value = index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "";
        }
        if (value.Length is < 10 or > 200 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Drive 폴더 링크를 입력하세요.");
        return value;
    }

    public async Task ConfigureAsync(bool enabled, string account, string folder, CancellationToken ct)
    {
        await _run.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = new DocumentDriveConfig { Enabled = enabled, Account = account.Trim(), FolderId = enabled ? FolderId(folder) : Settings.FolderId };
            if (enabled)
            {
                if (!settings.Account.Contains('@') || settings.Account.Any(char.IsWhiteSpace)) throw new ArgumentException("업로드 계정 이메일을 입력하세요.");
                await _client.ConnectAsync(settings, ct).ConfigureAwait(false);
            }
            _config.Update(c => c.UsbDocuments.Drive = settings);
            lock (_gate) _status = enabled ? "Drive 연결 확인 · 자동 업로드 사용" : "Drive 자동 업로드 꺼짐";
        }
        finally { _run.Release(); }
    }

    public void RetryFailed()
    {
        lock (_gate)
        {
            if (_stateBroken) return;
            foreach (var record in _records.Where(x => x.Status == "failed"))
            { record.Status = "queued"; record.Error = null; }
            Save();
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await RunOnceAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { lock (_gate) { _stateBroken = true; _status = "Drive 작업이 중단됐습니다. 업로드 기록과 PC 저장 공간을 확인하세요."; } }
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _run.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = Settings;
            lock (_gate) { if (_stateBroken || !settings.Enabled) return; }
            if (!_archive.Settings.Enabled) return;
            var snapshot = _archive.Snapshot(skip: _cursor);
            _cursor += snapshot.Items.Length;
            if (_cursor >= snapshot.Total) _cursor = 0;
            // At most ten new attempts per pass, regardless of catalog size.
            var attempts = 0;
            foreach (var item in snapshot.Items.Where(x => x.Status is "ready" or "duplicate"))
            {
                ct.ThrowIfCancellationRequested();
                if (item.Sha256 is null) continue;
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "drive-documents-v1\n" + settings.Account.ToLowerInvariant() + "\n" + settings.FolderId + "\n" + item.Sha256)));
                DocumentDriveUploadRecord record;
                lock (_gate)
                {
                    record = _records.FirstOrDefault(x => x.Key == key)!;
                    if (record is not null && record.Status != "queued") continue;
                    if (record is null) { record = new() { Key = key }; _records.Add(record); }
                    record.Status = "uploading"; Save();
                    _status = "Drive 업로드 중";
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    if (item.Size > 256L * 1024 * 1024) throw new DriveDocumentException("256 MiB를 초과하는 자료는 Drive에 수동 업로드하세요.");
                    await _client.ValidateAsync(settings, timeout.Token).ConfigureAwait(false);
                    var opened = _archive.OpenDownload(item.Id) ?? throw new IOException("보관 파일 없음");
                    await using var stream = opened.Stream;
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    var buffer = new byte[256 * 1024];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    { sha.AppendData(buffer, 0, count); md5.AppendData(buffer, 0, count); }
                    if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("보관 파일 내용 변경");
                    stream.Position = 0;
                    if (record.RemoteId.Length == 0)
                    {
                        var id = await _client.AllocateIdAsync(timeout.Token).ConfigureAwait(false);
                        lock (_gate) { record.RemoteId = id; Save(); }
                    }
                    await _client.UploadAsync(settings, item, stream, record.RemoteId,
                        Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(), timeout.Token).ConfigureAwait(false);
                    lock (_gate) { record.Status = "uploaded"; record.Error = null; Save(); _status = "Drive 업로드 완료"; }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lock (_gate)
                    {
                        record.Status = "failed";
                        record.Error = ex is DriveDocumentException ? ex.Message : "Drive 업로드 실패. 연결·권한·보관 파일을 확인하고 재시도하세요.";
                        Save(); _status = record.Error;
                    }
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                }
                if (++attempts >= 10) break;
            }
        }
        finally { _run.Release(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
            var temporary = _stateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records));
            File.Move(temporary, _stateFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _stateBroken = true; throw new IOException("Drive 상태 저장 실패", ex); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _stop.Dispose(); _run.Dispose();
    }
}

public sealed class DriveDocumentException(string message) : Exception(message);
