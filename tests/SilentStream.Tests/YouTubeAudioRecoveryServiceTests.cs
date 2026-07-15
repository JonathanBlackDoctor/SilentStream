using System.ComponentModel;
using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class YouTubeAudioRecoveryServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sstream-youtube-audio-").FullName;
    private readonly string _audioDirectory;
    private readonly string _temporaryRoot;
    private readonly string _catalogFile;
    private readonly DateTime _now = new(2026, 7, 15, 3, 0, 0, DateTimeKind.Utc);

    public YouTubeAudioRecoveryServiceTests()
    {
        _audioDirectory = Path.Combine(_root, "audio");
        _temporaryRoot = Path.Combine(_root, "recovery-temp");
        _catalogFile = Path.Combine(_root, "assets.json");
    }

    [Fact]
    public async Task Successful_recovery_downloads_encodes_caches_and_updates_catalog()
    {
        var catalog = CatalogWith(Asset("asset-1", "abcdefghijk"));
        var runner = new FakeRunner();
        var exporter = new FakeExporter(_audioDirectory);
        using var service = CreateService(runner, exporter, catalog);

        Assert.Equal(AudioRecoveryStatus.Queued, service.Start("asset-1").Status);
        var status = await WaitForTerminalAsync(service, "asset-1");

        Assert.Equal(AudioRecoveryStatus.Available, status.Status);
        var saved = Assert.IsType<PeriodAsset>(new PeriodAssetCatalog(_catalogFile).Find("asset-1"));
        Assert.NotNull(saved.AudioPath);
        Assert.True(File.Exists(saved.AudioPath));
        Assert.Equal(1, runner.Calls);
        Assert.Equal(1, exporter.Calls);
        AssertNoTemporaryDirectories();
    }

    [Fact]
    public async Task Duplicate_requests_for_one_asset_share_one_job()
    {
        var catalog = CatalogWith(Asset("asset-1", "abcdefghijk"));
        var runner = new FakeRunner { Blocker = NewBlocker() };
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        var first = service.Start("asset-1");
        var second = service.Start("asset-1");
        await WaitUntilAsync(() => runner.Calls == 1);

        Assert.Equal(AudioRecoveryStatus.Queued, first.Status);
        Assert.True(AudioRecoveryStatus.IsActive(second.Status));
        runner.Blocker.SetResult(true);
        Assert.Equal(AudioRecoveryStatus.Available,
            (await WaitForTerminalAsync(service, "asset-1")).Status);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Different_assets_are_recovered_one_at_a_time()
    {
        var catalog = CatalogWith(
            Asset("asset-1", "abcdefghijk"),
            Asset("asset-2", "lmnopqrstuv"));
        var runner = new FakeRunner { Blocker = NewBlocker() };
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        service.Start("asset-1");
        service.Start("asset-2");
        await WaitUntilAsync(() => runner.Calls == 1);

        var statuses = new[]
        {
            service.GetStatus("asset-1").Status,
            service.GetStatus("asset-2").Status,
        };
        Assert.Contains(AudioRecoveryStatus.Downloading, statuses);
        Assert.Contains(AudioRecoveryStatus.Queued, statuses);
        Assert.Equal(1, runner.MaxActive);
        runner.Blocker.SetResult(true);

        await WaitForTerminalAsync(service, "asset-1");
        await WaitForTerminalAsync(service, "asset-2");
        Assert.Equal(2, runner.Calls);
        Assert.Equal(1, runner.MaxActive);
    }

    [Fact]
    public void Existing_safe_cache_skips_external_tools()
    {
        Directory.CreateDirectory(_audioDirectory);
        var audioPath = Path.Combine(_audioDirectory, "asset-1.m4a");
        File.WriteAllBytes(audioPath, [1, 2, 3]);
        var catalog = CatalogWith(Asset("asset-1", "abcdefghijk") with { AudioPath = audioPath });
        var runner = new FakeRunner();
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        var status = service.Start("asset-1");

        Assert.Equal(AudioRecoveryStatus.Available, status.Status);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Invalid_catalogued_video_id_is_rejected_without_starting_a_process()
    {
        var catalog = CatalogWith(Asset("asset-1", "not-a-video-id"));
        var runner = new FakeRunner();
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        var status = service.Start("asset-1");

        Assert.Equal(AudioRecoveryStatus.Failed, status.Status);
        Assert.Contains("YouTube 영상 정보", status.Message);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Download_failure_is_classified_cleaned_and_retryable()
    {
        var catalog = CatalogWith(Asset("asset-1", "abcdefghijk"));
        var runner = new FakeRunner { FailuresRemaining = 1, FailureOutput = "ERROR: Private video" };
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        service.Start("asset-1");
        var failed = await WaitForTerminalAsync(service, "asset-1");

        Assert.Equal(AudioRecoveryStatus.Failed, failed.Status);
        Assert.Contains("비공개", failed.Message);
        AssertNoTemporaryDirectories();

        service.Start("asset-1");
        Assert.Equal(AudioRecoveryStatus.Available,
            (await WaitForTerminalAsync(service, "asset-1")).Status);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task Timeout_and_encoding_failure_leave_no_partial_asset()
    {
        var timeoutCatalog = CatalogWith(Asset("timeout", "abcdefghijk"));
        var timeoutRunner = new FakeRunner { ThrowCancellation = true };
        using (var timeoutService = CreateService(
                   timeoutRunner, new FakeExporter(_audioDirectory), timeoutCatalog))
        {
            timeoutService.Start("timeout");
            var status = await WaitForTerminalAsync(timeoutService, "timeout");
            Assert.Contains("제한시간", status.Message);
            Assert.Null(timeoutCatalog.Find("timeout")!.AudioPath);
        }
        AssertNoTemporaryDirectories();

        File.Delete(_catalogFile);
        var encodeCatalog = CatalogWith(Asset("encode", "abcdefghijk"));
        var exporter = new FakeExporter(_audioDirectory) { Fail = true };
        using (var encodeService = CreateService(new FakeRunner(), exporter, encodeCatalog))
        {
            encodeService.Start("encode");
            var status = await WaitForTerminalAsync(encodeService, "encode");
            Assert.Contains("변환", status.Message);
            Assert.Null(encodeCatalog.Find("encode")!.AudioPath);
        }
        AssertNoTemporaryDirectories();

        File.Delete(_catalogFile);
        var encodeTimeoutCatalog = CatalogWith(Asset("encode-timeout", "abcdefghijk"));
        var timeoutExporter = new FakeExporter(_audioDirectory) { ThrowCancellation = true };
        using (var encodeTimeoutService = CreateService(
                   new FakeRunner(), timeoutExporter, encodeTimeoutCatalog))
        {
            encodeTimeoutService.Start("encode-timeout");
            var status = await WaitForTerminalAsync(encodeTimeoutService, "encode-timeout");
            Assert.Contains("변환 시간이 초과", status.Message);
            Assert.Null(encodeTimeoutCatalog.Find("encode-timeout")!.AudioPath);
        }
        AssertNoTemporaryDirectories();
    }

    [Fact]
    public async Task Missing_downloader_is_reported_without_exposing_process_details()
    {
        var catalog = CatalogWith(Asset("asset-1", "abcdefghijk"));
        var runner = new FakeRunner { ThrowMissingTool = true };
        using var service = CreateService(runner, new FakeExporter(_audioDirectory), catalog);

        service.Start("asset-1");
        var status = await WaitForTerminalAsync(service, "asset-1");

        Assert.Equal(AudioRecoveryStatus.Failed, status.Status);
        Assert.Contains("복구 도구", status.Message);
        Assert.DoesNotContain("yt-dlp-test", status.Message);
        AssertNoTemporaryDirectories();
    }

    [Fact]
    public void Startup_removes_only_stale_recovery_subdirectories()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var stale = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "stale")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(_temporaryRoot, "fresh")).FullName;
        File.WriteAllText(Path.Combine(stale, "partial.webm"), "partial");
        Directory.SetLastWriteTimeUtc(stale, _now.AddHours(-25));
        Directory.SetLastWriteTimeUtc(fresh, _now.AddHours(-1));

        using var service = CreateService(
            new FakeRunner(), new FakeExporter(_audioDirectory), new PeriodAssetCatalog(_catalogFile));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Locator_prefers_a_bundled_binary_and_otherwise_uses_path_name()
    {
        var baseDirectory = Path.Combine(_root, "app");
        var exeName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

        Assert.Equal(exeName, YtDlpLocator.Resolve(baseDirectory));

        var bundled = Path.Combine(baseDirectory, "yt-dlp", exeName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllBytes(bundled, [1]);
        Assert.Equal(bundled, YtDlpLocator.Resolve(baseDirectory));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PeriodAssetCatalog CatalogWith(params PeriodAsset[] assets)
    {
        var catalog = new PeriodAssetCatalog(_catalogFile);
        foreach (var asset in assets)
        {
            catalog.Upsert(asset);
        }
        return catalog;
    }

    private static PeriodAsset Asset(string id, string videoId) =>
        new(id, new DateOnly(2026, 7, 15), 1, "1교시", VideoId: videoId);

    private YouTubeAudioRecoveryService CreateService(
        FakeRunner runner,
        FakeExporter exporter,
        IPeriodAssetCatalog catalog) =>
        new(
            runner,
            exporter,
            catalog,
            new LogService(),
            _audioDirectory,
            _temporaryRoot,
            "yt-dlp-test",
            TimeSpan.FromMilliseconds(250),
            () => _now);

    private static TaskCompletionSource<bool> NewBlocker() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void AssertNoTemporaryDirectories()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Assert.Empty(Directory.EnumerateDirectories(_temporaryRoot));
        }
    }

    private static async Task<AudioRecoverySnapshot> WaitForTerminalAsync(
        IYouTubeAudioRecoveryService service,
        string assetId)
    {
        AudioRecoverySnapshot? snapshot = null;
        await WaitUntilAsync(() =>
        {
            snapshot = service.GetStatus(assetId);
            return !AudioRecoveryStatus.IsActive(snapshot.Status) && snapshot.Status != AudioRecoveryStatus.Idle;
        });
        return snapshot!;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly object _failureGate = new();
        private int _active;
        private int _maxActive;
        private int _calls;

        public TaskCompletionSource<bool>? Blocker { get; set; }
        public int FailuresRemaining { get; set; }
        public string FailureOutput { get; set; } = "ERROR: unavailable";
        public bool ThrowCancellation { get; set; }
        public bool ThrowMissingTool { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int MaxActive => Volatile.Read(ref _maxActive);

        public async Task<(int ExitCode, string Output)> RunAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                if (Blocker is not null)
                {
                    await Blocker.Task.WaitAsync(ct);
                }
                if (ThrowCancellation)
                {
                    throw new OperationCanceledException(ct);
                }
                if (ThrowMissingTool)
                {
                    throw new Win32Exception(2, "yt-dlp-test was not found");
                }

                lock (_failureGate)
                {
                    if (FailuresRemaining > 0)
                    {
                        FailuresRemaining--;
                        return (1, FailureOutput);
                    }
                }

                var match = Regex.Match(arguments, "--output \\\"([^\\\"]+)\\\"");
                if (!match.Success)
                {
                    throw new InvalidOperationException("Output template was not supplied.");
                }
                var outputPath = match.Groups[1].Value.Replace("%(ext)s", "webm", StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, [1, 2, 3, 4]);
                return (0, string.Empty);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActive);
                if (active <= current || Interlocked.CompareExchange(ref _maxActive, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FakeExporter(string outputDirectory) : ILocalAudioExportService
    {
        private int _calls;
        public bool Fail { get; set; }
        public bool ThrowCancellation { get; set; }
        public int Calls => Volatile.Read(ref _calls);

        public Task<string?> ExportAsync(string sourceMediaPath, string assetId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Fail)
            {
                return Task.FromResult<string?>(null);
            }
            if (ThrowCancellation)
            {
                return Task.FromCanceled<string?>(new CancellationToken(canceled: true));
            }
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, assetId + ".m4a");
            File.WriteAllBytes(outputPath, [5, 6, 7, 8]);
            return Task.FromResult<string?>(outputPath);
        }
    }
}
