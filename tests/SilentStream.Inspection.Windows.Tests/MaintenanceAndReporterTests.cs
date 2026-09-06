using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class MaintenanceAndReporterTests
{
    [Fact]
    public void Maintenance_reservation_rejects_a_new_run_until_end_maintenance_releases_it()
    {
        using var fixture = new StoreFixture();
        var bundle = fixture.SaveBundle("maintenance-run");

        Assert.True(fixture.Store.TryBeginMaintenance(out var blockingRun));
        Assert.Null(blockingRun);
        try
        {
            Assert.False(fixture.Store.TryCreateRunWhenIdle(bundle, "operator", false, out var rejectedRun));
            Assert.Equal(string.Empty, rejectedRun);
            Assert.Empty(fixture.Store.ListRuns());
        }
        finally
        {
            fixture.Store.EndMaintenance();
        }

        Assert.True(fixture.Store.TryCreateRunWhenIdle(bundle, "operator", false, out var createdRun));
        Assert.False(string.IsNullOrWhiteSpace(createdRun));
        Assert.Equal(createdRun, fixture.Store.GetLatestRunId());
    }

    [Fact]
    public async Task Reporter_marks_startup_terminal_even_when_the_application_is_offline()
    {
        using var fixture = new StoreFixture();
        const string bridgeToken = "reporter-bridge-secret";
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.AppApiTokenMachineEnc = CoordinatorOptionsStore.ProtectMachine(bridgeToken);
        });
        var runId = fixture.CreateTerminalRun(InspectionRunStatus.Pass);
        var startup = new StartupUpdateGate();
        startup.MarkReady(true, "update complete");
        startup.MarkInspection("inspection-resumed", "resuming", runId);
        var offline = new CapturingOfflineHandler();
        using var reporter = new InspectionStatusReporter(
            fixture.Store,
            fixture.Options,
            new HandlerHttpClientFactory(offline),
            startup,
            NullLogger<InspectionStatusReporter>.Instance);

        await reporter.StartAsync(CancellationToken.None);
        try
        {
            var headers = await offline.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForPhaseAsync(startup, "completed", TimeSpan.FromSeconds(2));

            Assert.Equal(runId, startup.Snapshot.RunId);
            Assert.Equal("Bearer", headers.AuthorizationScheme);
            Assert.Equal(bridgeToken, headers.AuthorizationValue);
            Assert.Equal(bridgeToken, headers.BridgeValue);
        }
        finally
        {
            await reporter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reporter_reconciles_terminal_startup_state_before_fingerprint_deduplication()
    {
        using var fixture = new StoreFixture();
        var runId = fixture.CreateTerminalRun(InspectionRunStatus.Warning);
        var startup = new StartupUpdateGate();
        startup.MarkReady(true, "update complete");
        startup.MarkInspection("inspection-resumed", "resuming", runId);
        var online = new CountingOkHandler();
        using var reporter = new InspectionStatusReporter(
            fixture.Store,
            fixture.Options,
            new HandlerHttpClientFactory(online),
            startup,
            NullLogger<InspectionStatusReporter>.Instance);

        await reporter.StartAsync(CancellationToken.None);
        try
        {
            await online.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForPhaseAsync(startup, "completed-with-warning", TimeSpan.FromSeconds(2));
            Assert.Equal(1, online.Count);

            startup.MarkInspection("inspection-resumed", "service restarted", runId);
            await WaitForPhaseAsync(startup, "completed-with-warning", TimeSpan.FromSeconds(5));

            Assert.Equal(runId, startup.Snapshot.RunId);
            Assert.Equal(1, online.Count);
        }
        finally
        {
            await reporter.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForPhaseAsync(StartupUpdateGate startup, string phase, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!string.Equals(startup.Snapshot.Phase, phase, StringComparison.Ordinal))
            await Task.Delay(25, cancellation.Token);
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "SilentStreamMaintenanceReporterTests", Guid.NewGuid().ToString("N"));
        public CoordinatorOptionsStore Options { get; }
        public InspectionStore Store { get; }

        public StoreFixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CoordinatorOptionsStore(Root);
            Store = new InspectionStore(Options);
        }

        public StoredBundle SaveBundle(string id)
        {
            var manifest = new InspectionBundleManifest
            {
                BundleId = id,
                Version = "0.4.1",
                SigningKeyId = "test",
                Plan = new InspectionPlan
                {
                    Name = id,
                    Steps =
                    [
                        new InspectionStep
                        {
                            Id = "wait",
                            Type = InspectionStepType.Wait,
                            Input = JsonSerializer.SerializeToElement(new { seconds = 1 }, InspectionProtocol.Json)
                        }
                    ]
                }
            };
            Store.SaveBundle(new VerifiedInspectionBundle(manifest, Root));
            return new StoredBundle(manifest, Root);
        }

        public string CreateTerminalRun(InspectionRunStatus status)
        {
            var bundle = SaveBundle("reporter-" + Guid.NewGuid().ToString("N"));
            var runId = Store.CreateRun(bundle, "test", false);
            Store.UpdateStep(runId, "wait", status, 1, status.ToString());
            Store.UpdateRun(runId, status, status.ToString());
            return runId;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed record RequestHeaders(string? AuthorizationScheme, string? AuthorizationValue, string? BridgeValue);

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingOfflineHandler : HttpMessageHandler
    {
        public TaskCompletionSource<RequestHeaders> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-SilentStream-Inspection-Bridge", out var bridgeValues);
            Request.TrySetResult(new RequestHeaders(
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                bridgeValues?.SingleOrDefault()));
            throw new HttpRequestException("application offline");
        }
    }

    private sealed class CountingOkHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            FirstRequest.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
