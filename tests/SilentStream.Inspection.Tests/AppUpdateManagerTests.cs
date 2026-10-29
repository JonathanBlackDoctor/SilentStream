using SilentStream.App.Updates;
using SilentStream.Core.Contracts;
using Velopack;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class AppUpdateManagerTests
{
    [Fact]
    public async Task BackgroundDownloadStagesOnceWithoutArmingAnExitOrRestart()
    {
        var client = new UpdateClient();
        using var manager = new AppUpdateManager(new Log(), () => client);
        var announced = new List<string>();
        manager.UpdateStaged += announced.Add;

        await manager.CheckOnceAsync(CancellationToken.None);
        await manager.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, client.Downloads);
        Assert.Equal(new[] { "0.4.26" }, announced);
        Assert.Empty(client.ApplyCalls);
        Assert.Same(client.Update!.TargetFullRelease, client.UpdatePendingRestart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledDownloadDoesNotAnnounceOrArmAnUpdate(bool cancel)
    {
        var client = new UpdateClient { DownloadFailure = cancel
            ? new OperationCanceledException() : new IOException("download incomplete") };
        using var manager = new AppUpdateManager(new Log(), () => client);
        var announced = new List<string>();
        manager.UpdateStaged += announced.Add;

        await manager.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(announced);
        Assert.Empty(client.ApplyCalls);
        Assert.Null(client.UpdatePendingRestart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAppliesPendingOrNewUpdateAndRequiresRestart(bool alreadyDownloaded)
    {
        var client = new UpdateClient();
        if (alreadyDownloaded) client.UpdatePendingRestart = client.Update!.TargetFullRelease;
        using var manager = new AppUpdateManager(new Log(), () => client);

        Assert.True(await manager.PrepareStartupUpdateAsync());

        Assert.Equal(alreadyDownloaded ? 0 : 1, client.Downloads);
        var apply = Assert.Single(client.ApplyCalls);
        Assert.Same(client.Update!.TargetFullRelease, apply.Asset);
        Assert.True(apply.Silent);
        Assert.True(apply.Restart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupWithoutAnApplicableUpdateKeepsCurrentAppRunning(bool installed)
    {
        var client = new UpdateClient { IsInstalled = installed, Update = null };
        using var manager = new AppUpdateManager(new Log(), () => client);

        Assert.False(await manager.PrepareStartupUpdateAsync());

        Assert.Equal(0, client.Downloads);
        Assert.Empty(client.ApplyCalls);
    }

    private sealed class UpdateClient : IAppUpdateClient
    {
        public bool IsInstalled { get; init; } = true;
        public VelopackAsset? UpdatePendingRestart { get; set; }
        public UpdateInfo? Update { get; init; } = new(new VelopackAsset
        {
            Version = new SemanticVersion(0, 4, 26),
            FileName = "MediaCaptureHelper-0.4.26-full.nupkg"
        }, false, null, null);
        public Exception? DownloadFailure { get; init; }
        public int Downloads { get; private set; }
        public List<(VelopackAsset Asset, bool Silent, bool Restart)> ApplyCalls { get; } = [];
        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult(Update);
        public Task DownloadUpdatesAsync(UpdateInfo update, CancellationToken cancelToken)
        {
            Downloads++;
            if (DownloadFailure is not null) return Task.FromException(DownloadFailure);
            UpdatePendingRestart = update.TargetFullRelease;
            return Task.CompletedTask;
        }
        public void WaitExitThenApplyUpdates(VelopackAsset asset, bool silent, bool restart) =>
            ApplyCalls.Add((asset, silent, restart));
    }

    private sealed class Log : ILogService
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
