using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class AgentUpdateHostedServiceTests
{
    [Fact]
    public async Task Scan_ignores_old_and_invalid_files_then_selects_the_highest_valid_version()
    {
        using var fixture = new UpdateFixture();
        var old = fixture.CreateSignedUpdate("00-old.zip", "0.0.1");
        var invalid = Path.Combine(fixture.Options.IncomingAgentUpdatesPath, "01-invalid.zip");
        File.WriteAllBytes(invalid, "not a signed update"u8.ToArray());
        var lower = fixture.CreateSignedUpdate("02-valid-lower.zip", "90.1.0");
        var latest = fixture.CreateSignedUpdate("99-valid-latest.zip", "90.2.0");
        var broker = new FakeBroker((_, _, _, _) =>
            Task.FromResult(new BrokerResponse("response", true, "scheduled")));
        using var service = fixture.CreateService(broker);

        var result = await service.ProcessCandidatesAsync([old, invalid, lower, latest], CancellationToken.None);

        Assert.Equal(AgentUpdateHostedService.AgentUpdateScanResult.Scheduled, result);
        Assert.Equal(1, broker.CallCount);
        Assert.Equal("apply-inspection-update", broker.Action);
        Assert.Equal(Path.GetFullPath(latest + ".applying"), Path.GetFullPath(broker.PackagePath!));
        Assert.True(File.Exists(old + ".applying.ignored"));
        Assert.True(File.Exists(invalid + ".applying.failed"));
        Assert.True(File.Exists(lower + ".applying.superseded"));
        Assert.True(File.Exists(latest + ".applying"));
        Assert.False(File.Exists(latest + ".applying.failed"));
        fixture.Store.EndMaintenance();
    }

    [Fact]
    public async Task Transient_broker_failure_restores_the_signed_zip_and_releases_maintenance()
    {
        using var fixture = new UpdateFixture();
        var candidate = fixture.CreateSignedUpdate("retry.zip", "91.0.0");
        var broker = new FakeBroker((_, _, _, _) =>
            throw new IOException("broker pipe is not ready"));
        using var service = fixture.CreateService(broker);

        var result = await service.ProcessCandidatesAsync([candidate], CancellationToken.None);

        Assert.Equal(AgentUpdateHostedService.AgentUpdateScanResult.Deferred, result);
        Assert.Equal(1, broker.CallCount);
        Assert.True(File.Exists(candidate));
        Assert.False(File.Exists(candidate + ".applying"));
        Assert.False(File.Exists(candidate + ".applying.failed"));
        Assert.True(fixture.Store.TryBeginMaintenance(out var blockingRun));
        Assert.Null(blockingRun);
        fixture.Store.EndMaintenance();
    }

    [Fact]
    public async Task Successful_broker_handoff_keeps_maintenance_reserved_and_rejects_a_concurrent_run()
    {
        using var fixture = new UpdateFixture();
        var candidate = fixture.CreateSignedUpdate("scheduled.zip", "92.0.0");
        var runnable = fixture.SaveInspectionBundle("operator-run");
        var brokerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new FakeBroker(async (_, _, _, token) =>
        {
            brokerEntered.TrySetResult();
            await releaseBroker.Task.WaitAsync(token);
            return new BrokerResponse("response", true, "scheduled");
        });
        using var service = fixture.CreateService(broker);

        var processing = service.ProcessCandidatesAsync([candidate], CancellationToken.None);
        await brokerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(fixture.Store.TryCreateRunWhenIdle(runnable, "operator", false, out var rejectedRun));
            Assert.Equal(string.Empty, rejectedRun);
            Assert.Empty(fixture.Store.ListRuns());
        }
        finally
        {
            releaseBroker.TrySetResult();
        }

        Assert.Equal(AgentUpdateHostedService.AgentUpdateScanResult.Scheduled, await processing);
        Assert.False(fixture.Store.TryBeginMaintenance(out var blockingRun));
        Assert.Null(blockingRun);
        fixture.Store.EndMaintenance();
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "SilentStreamAgentUpdateTests", Guid.NewGuid().ToString("N"));
        public CoordinatorOptionsStore Options { get; }
        public InspectionStore Store { get; }

        public UpdateFixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CoordinatorOptionsStore(Root);
            Options.Update(options => options.PublicKeys["test"] =
                Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
            Store = new InspectionStore(Options);
        }

        public AgentUpdateHostedService CreateService(IBrokerClient broker) => new(
            Options,
            broker,
            Store,
            new StartupUpdateGate(),
            NullLogger<AgentUpdateHostedService>.Instance);

        public string CreateSignedUpdate(string fileName, string version)
        {
            var payloads = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["broker/SilentStreamInspectionBroker.exe"] = Encoding.UTF8.GetBytes($"broker-{version}"),
                ["coordinator/SilentStreamInspectionCoordinator.exe"] = Encoding.UTF8.GetBytes($"coordinator-{version}"),
                ["session/SilentStreamInspectionSessionAgent.exe"] = Encoding.UTF8.GetBytes($"session-{version}")
            };
            var manifest = new InspectionAgentUpdateManifest
            {
                Version = version,
                SigningKeyId = "test",
                Files = new SortedDictionary<string, string>(payloads.ToDictionary(
                    pair => pair.Key,
                    pair => Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()), StringComparer.Ordinal)
            };
            manifest = manifest with
            {
                Signature = Convert.ToBase64String(_key.SignData(
                    InspectionAgentUpdateVerifier.SignaturePayload(manifest), HashAlgorithmName.SHA256))
            };

            var path = Path.Combine(Options.IncomingAgentUpdatesPath, fileName);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var pair in payloads)
            {
                using var stream = archive.CreateEntry(pair.Key).Open();
                stream.Write(pair.Value);
            }
            using (var stream = archive.CreateEntry(InspectionAgentUpdateVerifier.ManifestName).Open())
                JsonSerializer.Serialize(stream, manifest, InspectionProtocol.Json);
            return path;
        }

        public StoredBundle SaveInspectionBundle(string id)
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

        public void Dispose()
        {
            _key.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed class FakeBroker(
        Func<string, object?, string, CancellationToken, Task<BrokerResponse>> send) : IBrokerClient
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public string? Action { get; private set; }
        public string? PackagePath { get; private set; }

        public Task<BrokerResponse> SendAsync(
            string action, object? payload, string confirmationId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Action = action;
            PackagePath = payload?.GetType().GetProperty("packagePath")?.GetValue(payload)?.ToString();
            return send(action, payload, confirmationId, cancellationToken);
        }
    }
}
