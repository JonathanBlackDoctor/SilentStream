using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class PeriodAssetCatalogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-assets-").FullName;
    private readonly string _catalogFile;
    private DateTime _now = new(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc);

    public PeriodAssetCatalogTests()
    {
        _catalogFile = Path.Combine(_directory, "period_assets.json");
    }

    [Fact]
    public void Upsert_persists_a_detached_newest_first_snapshot()
    {
        var catalog = Create();
        var older = catalog.Upsert(Asset("period-1", 1));
        Advance();
        var newer = catalog.Upsert(Asset("period-2", 2));

        var snapshot = catalog.Snapshot();

        Assert.Equal(["period-2", "period-1"], snapshot.Select(asset => asset.Id));
        Assert.Equal(DateTimeKind.Utc, older.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, newer.UpdatedAtUtc.Kind);

        // A new instance must read the data written by the first one.
        var reloaded = Create().Snapshot();
        Assert.Equal(snapshot, reloaded);
    }

    [Fact]
    public void Upsert_replaces_an_existing_asset_and_refreshes_its_timestamp()
    {
        var catalog = Create();
        var first = catalog.Upsert(Asset("period-1", 1) with { AudioPath = "first.m4a" });
        Advance();

        var updated = catalog.Upsert(Asset("period-1", 1) with
        {
            Title = "1교시 수정본",
            AudioPath = "replacement.m4a",
            CaptionStatus = PeriodAssetCaptionStatus.Unavailable
        });

        var found = Assert.IsType<PeriodAsset>(catalog.Find("period-1"));
        Assert.Single(catalog.Snapshot());
        Assert.Equal("1교시 수정본", found.Title);
        Assert.Equal("replacement.m4a", found.AudioPath);
        Assert.Equal(PeriodAssetCaptionStatus.Unavailable, found.CaptionStatus);
        Assert.True(updated.UpdatedAtUtc > first.UpdatedAtUtc);
    }

    [Fact]
    public void Upload_and_caption_updates_are_persisted_and_unknown_ids_return_false()
    {
        IPeriodAssetCatalog catalog = Create();
        catalog.Upsert(Asset("period-1", 1));
        Advance();

        Assert.True(catalog.MarkUploaded("period-1", "video-123"));
        Advance();
        Assert.True(catalog.MarkCaptionStatus(
            "period-1", PeriodAssetCaptionStatus.Available, "ko", "SRT saved"));
        Assert.False(catalog.MarkUploaded("missing", "video-404"));
        Assert.False(catalog.MarkCaptionStatus("missing", PeriodAssetCaptionStatus.Failed));

        var saved = Assert.IsType<PeriodAsset>(Create().Find("period-1"));
        Assert.Equal("video-123", saved.VideoId);
        Assert.Equal(PeriodAssetCaptionStatus.Available, saved.CaptionStatus);
        Assert.Equal("ko", saved.CaptionLanguage);
        Assert.Equal("SRT saved", saved.CaptionMessage);
        Assert.Equal(_now, saved.UpdatedAtUtc);
    }

    [Fact]
    public void Concurrent_upserts_do_not_lose_assets()
    {
        var catalog = Create();

        Parallel.For(1, 41, period =>
            catalog.Upsert(Asset($"period-{period:D2}", period)));

        var snapshot = catalog.Snapshot();
        Assert.Equal(40, snapshot.Count);
        Assert.Equal(40, snapshot.Select(asset => asset.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("period-40", snapshot[0].Id);
        Assert.Equal(40, Create().Snapshot().Count);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private PeriodAssetCatalog Create() => new(_catalogFile, () => _now);

    private static PeriodAsset Asset(string id, int periodNumber) =>
        new(
            id,
            new DateOnly(2026, 7, 13),
            periodNumber,
            $"{periodNumber}교시 - 2026-07-13",
            AudioPath: $"{id}.m4a");

    private void Advance() => _now = _now.AddTicks(1);
}
