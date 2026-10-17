using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using SilentStream.Core.UsbDocuments;
using SilentStream.Platform.Windows;

namespace SilentStream.Inspection.Broker;

/// <summary>Short leased ETW session. Bounded metadata only; never reads or copies USB contents.</summary>
public sealed class UsbReadCollector : BackgroundService
{
    private readonly object _gate = new();
    private readonly UsbVolumeMap _volumes = new();
    private readonly Queue<(long Sequence, UsbReadNotice Notice)> _events = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lease;
    private long _sequence, _dropped;
    private string _status = "꺼짐";
    private bool _failed;
    private TraceEventSession? _session;
    private Task? _consumer;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "MediaCaptureHelper", "SilentStreamInspectionBroker", "SilentStreamInspectionCoordinator",
      "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "MsMpEng", "MpDefenderCoreService" };

    public UsbDetectorBatch Poll(bool enabled, long cursor)
    {
        lock (_gate)
        {
            if (!enabled) { _lease = DateTimeOffset.MinValue; _failed = false; _events.Clear(); }
            else _lease = DateTimeOffset.UtcNow.AddSeconds(8);
            if (cursor > _sequence) cursor = 0;
            while (_events.TryPeek(out var old) && old.Sequence <= cursor) _events.Dequeue();
            var batch = _events.Where(e => e.Sequence > cursor).Take(64).ToArray();
            return new(_status, batch.Length == 0 ? _sequence : batch[^1].Sequence,
                _dropped + (_session?.Source.EventsLost ?? 0), batch.Select(e => e.Notice).ToArray());
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool active;
                lock (_gate) active = DateTimeOffset.UtcNow < _lease;
                if (active && !_failed)
                {
                    _volumes.Refresh();
                    if (_session is null) Start();
                    if (_consumer?.IsCompleted == true)
                    { lock (_gate) { _failed = true; _status = "감지 중단: 기능을 껐다 켜서 재시도하세요."; } Stop(); }
                }
                else if (_session is not null) Stop();
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Stop(); }
    }
    private void Start()
    {
        try
        {
            var session = new TraceEventSession("SilentStream-UsbDocuments-" + Environment.ProcessId);
            _session = session;
            session.StopOnDispose = true;
            session.Source.Kernel.FileIORead += data =>
            {
                if (data.IoSize <= 0 || string.IsNullOrEmpty(data.FileName) || !UsbDocumentArchive.IsDocument(data.FileName)) return;
                var resolved = _volumes.Resolve(data.FileName);
                if (resolved is null || !AllowedProcess(data.ProcessID)) return;
                var now = DateTimeOffset.UtcNow;
                lock (_gate)
                {
                    if (now >= _lease) return;
                    var key = resolved.Value.Path;
                    if (_recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
                    if (_recent.Count > 512)
                        foreach (var stale in _recent.Where(p => now - p.Value > TimeSpan.FromSeconds(2)).Select(p => p.Key).ToArray())
                            _recent.Remove(stale);
                    if (_recent.Count >= 1024) { _dropped++; return; }
                    _recent[key] = now;
                    if (_events.Count >= 256) { _events.Dequeue(); _dropped++; }
                    _events.Enqueue((++_sequence, new(key, resolved.Value.Connection, data.ProcessID, now)));
                }
            };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIO
                | KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.DiskFileIO | KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.Thread);
            _consumer = Task.Run(() =>
            {
                try { session.Source.Process(); }
                catch { lock (_gate) { if (ReferenceEquals(_session, session)) { _failed = true; _status = "파일 읽기 감지가 중단됐습니다."; } } }

            });
            lock (_gate) _status = "USB 문서 읽기 감지 중 · 미리보기 포함";
        }
        catch (Exception)
        {
            lock (_gate) { _failed = true; _status = "ETW 시작 실패: 관리자 서비스·권한을 확인하고 껐다 켜세요."; }
            Stop();
        }
    }
    private static bool AllowedProcess(int id)
    {
        if (id <= 4 || id == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(id);
            return process.SessionId != 0 && !Excluded.Contains(process.ProcessName);
        }
        catch { return false; }
    }
    private void Stop()
    {
        TraceEventSession? session;
        lock (_gate) { session = _session; if (session is not null) _dropped += session.Source.EventsLost; _session = null; }
        _consumer = null;
        try { session?.Dispose(); } catch { }
        if (!_failed) { lock (_gate) _status = "꺼짐"; }
    }
}
