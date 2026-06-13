using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class RecordingManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-rec-").FullName;
    private readonly ConfigStore _configStore;
    private DateTime _now = new(2026, 6, 12, 9, 30, 0);
    private long _freeBytes = long.MaxValue / 2;

    public RecordingManagerTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Recording.Folder = Path.Combine(_dir, "rec");
        config.Recording.MaxSizeGb = 100;
        config.Recording.RetentionDays = 7;
        config.Recording.MinFreeGb = 5;
        _configStore.Save(config);
    }

    private RecordingManager CreateManager() =>
        new(_configStore, new LogService(), () => _now, _ => _freeBytes);

    private string AddFile(string name, long sizeBytes, DateTime creation)
    {
        var folder = _configStore.Load().Recording.Folder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.SetLength(sizeBytes);
        }
        File.SetCreationTime(path, creation);
        return path;
    }

    [Fact]
    public void Session_file_name_follows_the_documented_pattern()
    {
        var manager = CreateManager();
        var path = manager.CreateSessionFilePath(new DateTime(2026, 6, 12, 9, 30, 0));

        Assert.Equal("SilentStream_REC_2026-06-12_0930.mp4", Path.GetFileName(path));
        Assert.Equal(_configStore.Load().Recording.Folder, Path.GetDirectoryName(path));
    }

    [Fact]
    public void Same_minute_restart_gets_a_part_suffix_instead_of_overwriting()
    {
        var manager = CreateManager();
        var start = new DateTime(2026, 6, 12, 9, 30, 0);
        AddFile("SilentStream_REC_2026-06-12_0930.mp4", 10, start);

        var path = manager.CreateSessionFilePath(start);

        Assert.Equal("SilentStream_REC_2026-06-12_0930_part2.mp4", Path.GetFileName(path));
    }

    [Fact]
    public async Task Files_past_retention_days_are_deleted()
    {
        var manager = CreateManager();
        var old = AddFile("SilentStream_REC_2026-06-01_0900.mp4", 10, _now.AddDays(-8));
        var fresh = AddFile("SilentStream_REC_2026-06-10_0900.mp4", 10, _now.AddDays(-2));

        await manager.EnforceRetentionAsync(CancellationToken.None);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Oldest_files_are_deleted_first_when_over_the_size_cap()
    {
        var config = _configStore.Load();
        config.Recording.MaxSizeGb = 0; // cap of 0 GB → everything but nothing fits
        _configStore.Save(config);

        var manager = CreateManager();
        var oldest = AddFile("SilentStream_REC_2026-06-10_0900.mp4", 1024, _now.AddDays(-2));
        var newest = AddFile("SilentStream_REC_2026-06-11_0900.mp4", 1024, _now.AddDays(-1));

        await manager.EnforceRetentionAsync(CancellationToken.None);

        // Both exceed a 0GB cap; deletion is oldest-first until under the cap.
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(newest));
    }

    [Fact]
    public async Task Current_session_file_is_never_cleaned_up()
    {
        var config = _configStore.Load();
        config.Recording.MaxSizeGb = 0;
        config.Recording.RetentionDays = 0;
        _configStore.Save(config);

        var manager = CreateManager();
        var current = manager.CreateSessionFilePath(_now);
        AddFile(Path.GetFileName(current), 2048, _now.AddDays(-1)); // looks old AND over cap
        var other = AddFile("SilentStream_REC_2026-06-11_0900.mp4", 2048, _now.AddDays(-1));

        await manager.EnforceRetentionAsync(CancellationToken.None);

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public async Task Low_disk_space_triggers_oldest_first_cleanup()
    {
        var manager = CreateManager();
        var oldest = AddFile("SilentStream_REC_2026-06-10_0900.mp4", 10, _now.AddDays(-2));
        var newest = AddFile("SilentStream_REC_2026-06-11_0900.mp4", 10, _now.AddDays(-1));

        var calls = 0;
        _freeBytes = 0;
        var managerLowDisk = new RecordingManager(_configStore, new LogService(), () => _now,
            _ => calls++ == 0 ? 0L : long.MaxValue / 2); // first probe: 0 free → delete one file

        await managerLowDisk.EnforceRetentionAsync(CancellationToken.None);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Status_reports_total_recording_bytes_and_free_space()
    {
        var manager = CreateManager();
        AddFile("SilentStream_REC_2026-06-10_0900.mp4", 1000, _now.AddDays(-2));
        AddFile("SilentStream_REC_2026-06-11_0900.mp4", 500, _now.AddDays(-1));
        _freeBytes = 123_456;

        var status = manager.GetStatus();

        Assert.Equal(1500, status.TotalUsedBytes);
        Assert.Equal(123_456, status.FreeDiskBytes);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
