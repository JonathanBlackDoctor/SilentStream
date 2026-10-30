using System.Security.Cryptography;
using System.Text;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class RecordingRetentionSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-retention-").FullName;
    private readonly ConfigStore _config;
    private readonly PeriodAssetCatalog _assets;
    private readonly QueueStub _queue = new();
    private static readonly DateTime Start = new(2026, 10, 28, 8, 25, 0);
    private RecordingRetentionGuard Guard => new(_assets, _queue, new PeriodScheduleStore());

    public RecordingRetentionSafetyTests()
    {
        _config = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Recording.Folder = _dir;
        config.Recording.RetentionDays = 0;
        config.Recording.MaxSizeGb = 0;
        _config.Save(config);
        _assets = new PeriodAssetCatalog(Path.Combine(_dir, "assets.json"));
    }

    private RecordingManager Manager() => new(_config, new LogService(), () => Start.AddDays(2), _ => 0, Guard);

    private string Key(RecordingSegment s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Path.GetFullPath(s.FilePath).ToUpperInvariant()}|{s.StartLocal.Ticks}|{Start.Ticks}|{Start.AddHours(1).Ticks}"))).ToLowerInvariant();

    [Fact]
    public async Task Quota_pending_and_unindexed_originals_survive_all_cleanup_triggers()
    {
        var manager = Manager();
        var path = manager.CreateSessionFilePath(Start);
        File.WriteAllBytes(path, new byte[4096]);
        File.SetLastWriteTime(path, Start.AddHours(1));
        manager.CreateSessionFilePath(Start.AddHours(1));
        var unknown = Path.Combine(_dir, "MediaCaptureHelper_REC_2026-10-27_0800.mp4");
        File.WriteAllBytes(unknown, new byte[4096]);
        var segment = new RecordingSegment(path, Start, Start.AddHours(1));
        _queue.Jobs.Add(new UploadJob("one", "cut.mp4", "one", new DateOnly(2026, 10, 28), 1,
            UploadJobStatus.Pending, 0, null, SourceKey: Key(segment)));
        await manager.EnforceRetentionAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(unknown));
    }

    [Theory]
    [InlineData(0, 4096, false)]
    [InlineData(1, 1217, false)]
    [InlineData(1, 4096, true)]
    public void Only_matching_validated_receipts_release_a_completed_recording(int validation, long size, bool expected)
    {
        var segment = new RecordingSegment(Path.Combine(_dir, "source.mp4"), Start, Start.AddHours(1));
        _assets.Upsert(new PeriodAsset("one", new DateOnly(2026, 10, 28), 1, "one", VideoId: "video",
            SourceKey: Key(segment), UploadedFileSizeBytes: size, UploadValidationVersion: validation));
        Assert.Equal(expected, Guard.CanDelete(segment));
        Assert.False(Guard.CanDelete(segment with { EndLocal = Start.AddHours(2) })); // missing next period
    }

    [Fact]
    public void Reboot_does_not_extend_yesterdays_recording_through_the_downtime()
    {
        var manager = Manager();
        var path = manager.CreateSessionFilePath(Start);
        File.WriteAllBytes(path, new byte[4096]);
        File.SetLastWriteTime(path, Start.AddMinutes(30));
        var rebooted = Manager();
        rebooted.CreateSessionFilePath(Start.AddDays(1));
        Assert.Empty(rebooted.GetSegments(Start.AddHours(1), Start.AddHours(2)));
        Assert.Equal(Start.AddMinutes(30), Assert.Single(rebooted.GetSegments(Start, Start.AddHours(1))).EndLocal);
    }

    [Fact]
    public void Config_corruption_retains_room_and_recording_folder_across_restart()
    {
        var config = _config.Load();
        config.DeviceName = "M112-preserve";
        _config.Save(config);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{broken");
        var restarted = new ConfigStore(Path.Combine(_dir, "config.json")).Load();
        Assert.Equal("M112-preserve", restarted.DeviceName);
        Assert.Equal(_dir, restarted.Recording.Folder);
    }

    private sealed class QueueStub : IUploadQueue
    {
        public List<UploadJob> Jobs { get; } = [];
        public void Enqueue(UploadJob job) => Jobs.Add(job);
        public IReadOnlyList<UploadJob> Snapshot() => Jobs.ToArray();
        public void Start(CancellationToken ct) { }
    }
    public void Dispose() => Directory.Delete(_dir, true);
}
