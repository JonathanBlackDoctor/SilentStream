using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.UsbDocuments;
using Xunit;

namespace SilentStream.Tests;

public sealed class UsbDocumentArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "silentstream-usb-test-" + Guid.NewGuid().ToString("N"));
    private readonly Config _config = new();
    private UsbDocumentArchive? _archive;
    public UsbDocumentArchiveTests()
    {
        Directory.CreateDirectory(_root);
        _config.Value.UsbDocuments = new() { Enabled = true, Folder = Path.Combine(_root, "archive") };
    }
    private UsbDocumentArchive Open(long speed = 100_000_000) => _archive = new(_config, Path.Combine(_root, "catalog.json"), speed);
    private string Source(string name, byte[]? bytes = null)
    {
        var file = Path.Combine(_root, name);
        File.WriteAllBytes(file, bytes ?? [1, 2, 3, 4]); return file;
    }
    private static UsbReadNotice Notice(string path, string connection = "usb-1") =>
        new(path, connection, 100, DateTimeOffset.Parse("2026-10-17T15:01:00Z"));
    private static async Task<UsbDocumentInfo> Finished(UsbDocumentArchive archive, string? name = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var found = archive.Snapshot().Items.FirstOrDefault(i => (name is null || i.Name == name) && i.Status is "ready" or "duplicate" or "failed");
            if (found is not null) return found;
            await Task.Delay(10, timeout.Token);
        }
    }
    [Fact]
    public async Task CopiesBytesAndHashAndUsesKoreaDateWithoutExposingPaths()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000); var file = Source("강의.pdf", bytes);
        using var archive = Open(); archive.Observe(Notice(file));
        var item = await Finished(archive);
        Assert.Equal("ready", item.Status); Assert.Equal("2026-10-18", item.Date);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), item.Sha256);
        Assert.DoesNotContain(_root, JsonSerializer.Serialize(archive.Snapshot()));
        var open = archive.OpenDownload(item.Id)!.Value;
        await using var stream = open.Stream;
        using var output = new MemoryStream(); await stream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
    }
    [Fact]
    public async Task DeduplicatesContentAndPreservesSameNameWithDifferentContents()
    {
        using var archive = Open();
        var file = Source("slides.pptx");
        archive.Observe(Notice(file)); await Finished(archive);
        archive.Observe(Notice(Source("second.pptx")));
        Assert.Equal("duplicate", (await Finished(archive, "second.pptx")).Status);
        File.WriteAllBytes(file, [9, 8, 7, 6, 5]);
        archive.Observe(Notice(file, "usb-2"));
        using var timeout = new CancellationTokenSource(5000);
        while (archive.Snapshot().Items.Count(i => i.Status == "ready") != 2) await Task.Delay(10, timeout.Token);
        Assert.Equal(2, Directory.GetFiles(_config.Value.UsbDocuments.Folder, "*.pptx", SearchOption.AllDirectories).Length);
    }
    [Fact]
    public void DefaultsEnabledAndIgnoresNonDocuments()
    {
        Assert.True(new UsbDocumentConfig().Enabled);
        using var archive = Open();
        archive.Observe(Notice(Source("other.txt"))); Assert.Empty(archive.Snapshot().Items);
        archive.Configure(false); archive.Observe(Notice(Source("a.pdf"))); Assert.Empty(archive.Snapshot().Items);
    }
    [Fact]
    public async Task RepeatedReadIsSuppressedAndMissingSourceOnlyRetriesOnChange()
    {
        using var archive = Open();
        var path = Path.Combine(_root, "removed.pdf");
        archive.Observe(Notice(path));
        var failed = await Finished(archive); Assert.Equal("failed", failed.Status);
        for (var i = 0; i < 10; i++) archive.Observe(Notice(path));
        Assert.Single(archive.Snapshot().Items);
        Source("removed.pdf");
        Assert.True(archive.Retry(failed.Id));
        var ready = await Finished(archive); Assert.Equal("ready", ready.Status);
    }
    [Fact]
    public async Task LockedSourceFailsAndCanBeExplicitlyRetried()
    {
        using var archive = Open(); var path = Source("locked.pdf");
        string id;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            archive.Observe(Notice(path));
            var failure = await Finished(archive); Assert.Equal("failed", failure.Status); id = failure.Id;
        }
        Assert.True(archive.Retry(id)); Assert.Equal("ready", (await Finished(archive)).Status);
    }
    [Fact]
    public async Task ChunkProtocolReassemblesBytesAndRejectsBadOffsetsAndIds()
    {
        using var archive = Open(); var bytes = RandomNumberGenerator.GetBytes(UsbDocumentArchive.ChunkSize * 2 + 7);
        archive.Observe(Notice(Source("big.PDF", bytes))); var item = await Finished(archive);
        using var output = new MemoryStream();
        long offset = 0; UsbDocumentChunk chunk;
        do
        {
            chunk = (await archive.ReadChunkAsync(item.Id, offset, default))!;
            var data = Convert.FromBase64String(chunk.Data);
            Assert.True(data.Length <= UsbDocumentArchive.ChunkSize);
            output.Write(data); offset += data.Length;
        } while (!chunk.End);
        Assert.Equal(bytes, output.ToArray());
        Assert.Null(archive.OpenDownload("../catalog.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => archive.ReadChunkAsync(item.Id, 1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => archive.ReadChunkAsync(item.Id, -1, default));
    }
    [Fact]
    public async Task DisableCancelsCopyAndNeverPublishesPartialFile()
    {
        using var archive = Open(100_000);
        archive.Observe(Notice(Source("slow.pdf", new byte[500_000])));
        using var timeout = new CancellationTokenSource(5000);
        while (archive.Snapshot().Items[0].Status != "copying") await Task.Delay(5, timeout.Token);
        var id = archive.Snapshot().Items[0].Id;
        Assert.Null(archive.OpenDownload(id));
        archive.Configure(false);
        Assert.Equal("failed", (await Finished(archive)).Status);
        Assert.Empty(Directory.GetFiles(_config.Value.UsbDocuments.Folder, "*.partial", SearchOption.AllDirectories));
    }
    [Fact]
    public void CorruptCatalogIsPreservedAndStopsCollection()
    {
        var file = Path.Combine(_root, "catalog.json"); File.WriteAllText(file, "{broken");
        using var archive = Open(); archive.Observe(Notice(Source("a.pdf")));
        Assert.Empty(archive.Snapshot().Items); Assert.Contains("읽을 수 없습니다", archive.Snapshot().DetectorStatus);
        Assert.Equal("{broken", File.ReadAllText(file));
    }
    [Fact]
    public async Task CompletedCatalogSurvivesRestartAndFreshHeartbeatIsRequired()
    {
        string id;
        using (var archive = Open())
        {
            archive.Observe(Notice(Source("a.ppt"))); id = (await Finished(archive)).Id;
            archive.Heartbeat("감지 중", 2); Assert.Equal(2, archive.Snapshot().Dropped);
        }
        using var restored = Open();
        Assert.Equal(id, Assert.Single(restored.Snapshot().Items).Id);
        Assert.Contains("연결 끊김", restored.Snapshot().DetectorStatus);
    }
    [Fact]
    public async Task InsufficientSpaceFailsWithoutPublishingOrRetryLoop()
    {
        using var archive = new UsbDocumentArchive(_config, Path.Combine(_root,"catalog.json"), freeSpace: _ => 0);
        archive.Observe(Notice(Source("space.pdf")));
        var failed = await Finished(archive);
        Assert.Equal("failed", failed.Status);
        Assert.Null(archive.OpenDownload(failed.Id));
        archive.Observe(Notice(Path.Combine(_root,"space.pdf")));
        Assert.Single(archive.Snapshot().Items);
    }
    [Fact]
    public void RestartMarksInterruptedWorkFailedAndCleansOnlyItsOwnedPartial()
    {
        var entry = new UsbDocumentEntry { Source = Source("crash.pdf"), Name = "crash.pdf", Status = "copying" };
        entry.TemporaryPath = Path.Combine(_root,"." + entry.Id + ".partial");
        File.WriteAllText(entry.TemporaryPath,"partial");
        var unrelated = Path.Combine(_root,"keep.partial");File.WriteAllText(unrelated,"keep");
        File.WriteAllText(Path.Combine(_root,"catalog.json"),JsonSerializer.Serialize(new[]{entry}));
        using var archive = Open();
        Assert.Equal("failed", Assert.Single(archive.Snapshot().Items).Status);
        Assert.False(File.Exists(entry.TemporaryPath));
        Assert.True(File.Exists(unrelated));
    }
    public void Dispose()
    {
        // Tests own this unique directory and never touch user USB documents.
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Config : IConfigStore
    {
        public AppConfig Value = new();
        public AppConfig Load() => Value;
        public void Save(AppConfig value) => Value = value;
        public void Update(Action<AppConfig> update) => update(Value);
    }
}
