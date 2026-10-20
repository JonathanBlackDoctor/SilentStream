using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.UsbDocuments;
using Xunit;

namespace SilentStream.Tests;

public sealed class UsbDocumentRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usb-retention-" + Guid.NewGuid().ToString("N"));
    private readonly Config _config = new();
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-01T03:00:00Z");
    private UsbDocumentArchive? _archive;
    private string Catalog => Path.Combine(_root, "catalog.json");
    public UsbDocumentRetentionTests()
    {
        Directory.CreateDirectory(_root);
        _config.Value.UsbDocuments.Folder = Path.Combine(_root, "archive");
    }
    private UsbDocumentArchive Open(bool automatic = false) => _archive = new(_config, Catalog, 100_000_000,
        utcNow: () => _now, startRetentionWorker: automatic);
    private async Task<UsbDocumentInfo> Add(UsbDocumentArchive archive, string name = "lecture.pdf", byte[]? bytes = null)
    {
        var source = Path.Combine(_root, name); File.WriteAllBytes(source, bytes ?? [1, 2, 3]);
        archive.Observe(new(source, Guid.NewGuid().ToString("N"), 1, _now));
        using var timeout = new CancellationTokenSource(5000);
        while (true)
        {
            var item = archive.Snapshot().Items.FirstOrDefault(x => x.Name == name && x.Status is "ready" or "duplicate");
            if (item is not null) return item;
            await Task.Delay(10, timeout.Token);
        }
    }
    private string ArchivedFile() => Directory.GetFiles(_config.Value.UsbDocuments.Folder, "*.pdf", SearchOption.AllDirectories).Single();
    [Fact]
    public async Task ExactSevenDayBoundaryRemovesOwnedFileCatalogAndEmptyDateFolder()
    {
        using var archive = Open(); await Add(archive); var file = ArchivedFile(); var initial = _now;
        _now = initial.AddDays(7).AddTicks(-1);
        Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(file));
        _now = initial.AddDays(7);
        Assert.Equal(1, archive.PruneExpired()); Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
        Assert.Empty(archive.Snapshot().Items); Assert.Equal("[]", File.ReadAllText(Catalog));
        Assert.True(File.Exists(Path.Combine(_root, "lecture.pdf")));
    }
    [Fact]
    public async Task NewerDuplicateReferenceKeepsSharedCopyUntilItsOwnExpiry()
    {
        using var archive = Open(); await Add(archive); var original = ArchivedFile();
        _now = _now.AddDays(6); Assert.Equal("duplicate", (await Add(archive, "same-content.pdf")).Status);
        _now = _now.AddDays(1); Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(original));
        _now = _now.AddDays(6); Assert.Equal(1, archive.PruneExpired()); Assert.False(File.Exists(original));
        Assert.Empty(archive.Snapshot().Items);
    }
    [Fact]
    public async Task ActiveReadPreventsDeletionUntilStreamIsClosed()
    {
        using var archive = Open(); var item = await Add(archive); var path = ArchivedFile();
        var opened = archive.OpenDownload(item.Id)!.Value;
        _now = _now.AddDays(8);
        Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(path));
        await opened.Stream.DisposeAsync();
        Assert.Equal(1, archive.PruneExpired()); Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task AllReadersMustCloseAndDoubleDisposeDoesNotReleaseAnotherReader()
    {
        using var archive = Open(); var item = await Add(archive);
        var first = archive.OpenDownload(item.Id)!.Value.Stream; var second = archive.OpenDownload(item.Id)!.Value.Stream;
        _now = _now.AddDays(8); first.Dispose(); first.Dispose();
        Assert.Equal(0, archive.PruneExpired()); second.Dispose(); Assert.Equal(1, archive.PruneExpired());
    }
    [Fact]
    public async Task ChunkDownloadGetsGraceBetweenSeparateChunkRequests()
    {
        using var archive = Open(); var item = await Add(archive);
        _now = _now.AddDays(8); Assert.NotNull(await archive.ReadChunkAsync(item.Id, 0, default));
        Assert.Equal(0, archive.PruneExpired());
        _now = _now.AddMinutes(2); Assert.Equal(1, archive.PruneExpired());
    }
    [Fact]
    public async Task RetentionAlsoRunsWhenCollectionAndDriveAreDisabled()
    {
        using var archive = Open(); await Add(archive);
        _config.Value.UsbDocuments.Enabled = false; _config.Value.UsbDocuments.Drive.Enabled = false;
        _now = _now.AddDays(8); Assert.Equal(1, archive.PruneExpired());
    }
    [Fact]
    public async Task ExistingCatalogFallsBackToDetectionTimeAndPrunesOnStartup()
    {
        using (var archive = Open()) await Add(archive);
        var entries = JsonSerializer.Deserialize<UsbDocumentEntry[]>(File.ReadAllText(Catalog))!;
        foreach (var entry in entries) entry.ArchivedAtUtc = null;
        File.WriteAllText(Catalog, JsonSerializer.Serialize(entries)); _now = _now.AddDays(8);
        using var restored = Open(automatic: true);
        using var timeout = new CancellationTokenSource(5000);
        while (restored.Snapshot().Total != 0) await Task.Delay(10, timeout.Token);
        Assert.Empty(Directory.GetFiles(_config.Value.UsbDocuments.Folder, "*.pdf", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task UnrelatedFilesAndUserModifiedArchivesArePreserved()
    {
        using var archive = Open(); await Add(archive); var owned = ArchivedFile();
        var unrelated = Path.Combine(Path.GetDirectoryName(owned)!, "keep.pdf"); File.WriteAllText(unrelated, "user file");
        File.WriteAllText(owned, "manually modified, longer content"); _now = _now.AddDays(8);
        Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(owned)); Assert.True(File.Exists(unrelated));
    }
    [Fact]
    public async Task AlreadyMissingArchiveOnlyRemovesItsMetadata()
    {
        using var archive = Open(); await Add(archive); File.Delete(ArchivedFile()); _now = _now.AddDays(8);
        Assert.Equal(1, archive.PruneExpired()); Assert.Empty(archive.Snapshot().Items);
    }
    [Fact]
    public async Task ChangingRootDoesNotDeleteFilesInThePreviousFolder()
    {
        using var archive = Open(); await Add(archive); var file = ArchivedFile();
        _config.Value.UsbDocuments.Folder = Path.Combine(_root, "new-root"); _now = _now.AddDays(8);
        Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task ExpiredFingerprintCanBeCollectedAgain()
    {
        using var archive = Open(); await Add(archive); _now = _now.AddDays(8);
        Assert.Equal(1, archive.PruneExpired()); await Add(archive);
        Assert.Single(archive.Snapshot().Items); Assert.Equal(0, archive.PruneExpired());
    }
    [Theory]
    [InlineData("outside")]
    [InlineData("wrong-generated-name")]
    [InlineData("traversal-date")]
    public void RejectsUnownedAndOutsidePathsInCatalog(string kind)
    {
        var entry = new UsbDocumentEntry { Name = "lecture.pdf", Date = "2026-10-01", Status = "ready", DetectedAtUtc = _now.AddDays(-8) };
        var directory = kind == "outside" ? Path.Combine(_root, "outside", entry.Date) : Path.Combine(_config.Value.UsbDocuments.Folder, entry.Date);
        Directory.CreateDirectory(directory);
        var name = kind == "wrong-generated-name" ? "keep.pdf" : "lecture_" + entry.Id + ".pdf";
        entry.Destination = Path.Combine(directory, name); File.WriteAllBytes(entry.Destination, [1, 2, 3]);
        entry.Size = 3; entry.DestinationWriteTimeUtc = File.GetLastWriteTimeUtc(entry.Destination);
        if (kind == "traversal-date") entry.Date = "../2026-10-01";
        File.WriteAllText(Catalog, JsonSerializer.Serialize(new[] { entry }));
        using var archive = Open(); Assert.Equal(0, archive.PruneExpired()); Assert.True(File.Exists(entry.Destination));
    }
    [Fact]
    public void CorruptCatalogCannotStartDestructiveCleanup()
    {
        File.WriteAllText(Catalog, "{bad"); using var archive = Open(); Assert.Equal(0, archive.PruneExpired());
        Assert.Equal("{bad", File.ReadAllText(Catalog));
    }
    private sealed class Config : IConfigStore
    { public AppConfig Value = new(); public AppConfig Load() => Value; public void Save(AppConfig value) => Value = value; public void Update(Action<AppConfig> mutate) => mutate(Value); }
    public void Dispose()
    {
        // The fixture owns this unique temporary directory, with no user data.
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
