using SilentStream.App.Remote;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.Safety;
using Xunit;

namespace SilentStream.Tests;

public sealed class RemotePowerIntentTests
{
    [Fact]
    public async Task Cancellation_during_encoder_flush_suppresses_shutdown_without_restart_or_live_resume()
    {
        var directory = Directory.CreateTempSubdirectory("sstream-power-intent-").FullName;
        try
        {
            var intent = new ApplicationSafetySession(directory, "boot-one");
            Assert.True(intent.Begin(1, 10, true, DateTimeOffset.UtcNow));
            Assert.True(intent.SetDesired(DesiredRunMode.RecordingOnly, "operator"));
            var pipeline = new Pipeline();
            using var power = new RemoteSystemShutdownService(pipeline, new LogService(), new SafeProbe(), intent, directory);
            var prepared = await power.PrepareAsync("operator", CancellationToken.None);
            Assert.True(prepared.Ok);
            await Task.Delay(prepared.NotBeforeUtc!.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50));
            var confirmation = power.ConfirmAsync(prepared.ConfirmationId, "operator", CancellationToken.None);
            await pipeline.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(intent.StartAllowed);
            Assert.Null(intent.BeginTransition(ApplicationTransition.Restart, "competing restart"));
            var cancel = await power.CancelAsync("operator", CancellationToken.None);
            Assert.True(cancel.Ok);
            Assert.False((await confirmation.WaitAsync(TimeSpan.FromSeconds(3))).Ok);
            Assert.False(power.WindowsShutdownPending);
            Assert.True(intent.StartAllowed);
            Assert.Equal(DesiredRunMode.RecordingOnly, intent.DesiredMode);
            Assert.Equal(0, pipeline.StartCalls);
            Assert.False(pipeline.Flushed.Task.IsCompleted);
            pipeline.Flushed.SetResult(); // Cancellation did not interrupt the actual encoder flush.
            var audit = File.ReadAllText(Path.Combine(directory, "system-power-audit.jsonl"));
            Assert.Contains("finalization-cancel", audit);
            Assert.DoesNotContain("\"stage\":\"scheduled\"", audit);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class SafeProbe : ISystemPowerSafetyProbe
    {
        public Task<SystemPowerSafetyAssessment> InspectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SystemPowerSafetyAssessment(true, [], []));
    }
    private sealed class Pipeline : IStreamOrchestrator
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls;
        public StreamState State => StreamState.Stopping;
        public QualityStatus CurrentQuality => default!;
        public event EventHandler<StreamState> StateChanged { add { } remove { } }
        public event EventHandler<MetricsSnapshot> MetricsUpdated { add { } remove { } }
        public event EventHandler<QualityStatus> QualityChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) { StartCalls++; return Task.CompletedTask; }
        public Task StopAsync() => throw new InvalidOperationException("Manual stop would discard desired recording-only mode.");
        public Task StopForMaintenanceAsync() { Stopping.TrySetResult(); return Flushed.Task; }
        public Task StopStreamingKeepRecordingAsync() => Task.CompletedTask;
        public Task<bool> ForceRetryAsync() => Task.FromResult(false);
    }
}
