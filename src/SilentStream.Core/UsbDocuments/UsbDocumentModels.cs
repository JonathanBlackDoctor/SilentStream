namespace SilentStream.Core.UsbDocuments;

public sealed class UsbDocumentConfig
{
    public bool Enabled { get; set; } = true;
    public string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SilentStream", "USB 자료");
}
public sealed record UsbReadNotice(string Path, string ConnectionId, int ProcessId, DateTimeOffset ReadAtUtc);
public sealed record UsbDetectorBatch(string Status, long Cursor, long Dropped, UsbReadNotice[] Events);
public sealed record UsbDocumentInfo(string Id, string Name, string Date, string Status, long Size,
    long CopiedBytes, string? Sha256, string? Error, DateTimeOffset DetectedAtUtc);
public sealed record UsbDocumentSnapshot(bool Supported, bool Enabled, string DetectorStatus, long Dropped,
    UsbDocumentInfo[] Items, int Total);
public sealed record UsbDocumentChunk(string Id, string Name, long Size, long Offset, string Sha256,
    string Data, bool End);
public sealed class UsbDocumentEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Destination { get; set; } = "";
    public string TemporaryPath { get; set; } = "";
    public DateTime DestinationWriteTimeUtc { get; set; }
    public string Name { get; set; } = "";
    public string Date { get; set; } = "";
    public string Status { get; set; } = "queued";
    public long Size { get; set; }
    public long CopiedBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
    public UsbDocumentInfo Public() => new(Id, Name, Date, Status, Size, CopiedBytes, Sha256, Error, DetectedAtUtc);
}
