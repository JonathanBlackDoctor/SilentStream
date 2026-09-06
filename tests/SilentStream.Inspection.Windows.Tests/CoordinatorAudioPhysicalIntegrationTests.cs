using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class CoordinatorAudioPhysicalIntegrationTests
{
    [Fact]
    public async Task Readiness_only_silence_then_resolved_physical_probe_passes_the_run()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = ["configured-mic"];
        });
        var session = new RecordingSessionAgentClient(call =>
        {
            Assert.Equal("audio-self-test", call.Action);
            var endpointId = call.RequiredPayload.GetProperty("captureEndpointId").GetString();
            return PhysicalResponse(endpointId!, passed: true);
        });
        var response = AudioResponse(Microphone(
            "mic:configured", "configured-mic", "resolved-wasapi-m113", signalWarning: true));
        var readiness = Step("microphone-path", new
        {
            captureReadyTimeoutSeconds = 0,
            signalSettleSeconds = 0,
            readinessOnly = true
        });
        var physical = Step("final-audio", new
        {
            captureReadyTimeoutSeconds = 0,
            signalSettleSeconds = 0,
            physical = true,
            physicalTimeoutSeconds = 8
        }) with { DependsOn = [readiness.Id] };

        var runId = await RunAsync(fixture, session, response, readiness, physical);

        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetSteps(runId)[readiness.Id].Status);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetSteps(runId)[physical.Id].Status);

        var readinessOutput = fixture.Store.GetStepOutput(runId, readiness.Id)!.Value;
        Assert.True(readinessOutput.GetProperty("ambientSignalWarning").GetBoolean());
        Assert.False(readinessOutput.GetProperty("micWarning").GetBoolean());
        Assert.Equal("capture-ready", readinessOutput.GetProperty("verdict").GetString());

        var physicalOutput = fixture.Store.GetStepOutput(runId, physical.Id)!.Value;
        Assert.True(physicalOutput.GetProperty("ambientSignalWarning").GetBoolean());
        Assert.False(physicalOutput.GetProperty("micWarning").GetBoolean());
        Assert.True(physicalOutput.GetProperty("physicalPassed").GetBoolean());
        Assert.Equal("physical-path-verified", physicalOutput.GetProperty("verdict").GetString());

        var call = Assert.Single(session.AudioCalls);
        Assert.Equal("resolved-wasapi-m113", call.RequiredPayload.GetProperty("captureEndpointId").GetString());
        Assert.Equal(8, call.RequiredPayload.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Physical_probe_falls_back_to_configured_device_id_when_resolved_id_is_missing()
    {
        using var fixture = RequiredM113Fixture("configured-mic");
        var session = new RecordingSessionAgentClient(call =>
            PhysicalResponse(call.RequiredPayload.GetProperty("captureEndpointId").GetString()!, passed: true));
        var response = AudioResponse(Microphone(
            "mic:configured", "configured-mic", resolvedDeviceId: null, signalWarning: false));

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetStoredRun(runId)!.Status);
        var call = Assert.Single(session.AudioCalls);
        Assert.Equal("configured-mic", call.RequiredPayload.GetProperty("captureEndpointId").GetString());
    }

    [Theory]
    [InlineData("reported-failure")]
    [InlineData("agent-error")]
    [InlineData("loopback")]
    [InlineData("endpoint-mismatch")]
    public async Task Failed_agent_or_loopback_physical_result_can_never_pass(string mode)
    {
        using var fixture = RequiredM113Fixture("configured-mic");
        var session = new RecordingSessionAgentClient(call => mode switch
        {
            "agent-error" => new SessionResponse("test", false, "session agent unavailable"),
            "loopback" => PhysicalResponse("resolved-wasapi-m113", passed: true, isLoopback: true,
                reason: "loopback-endpoint"),
            "endpoint-mismatch" => PhysicalResponse("a-different-endpoint", passed: true),
            _ => PhysicalResponse("resolved-wasapi-m113", passed: false, reason: "tone-not-detected")
        });
        var response = AudioResponse(Microphone(
            "mic:configured", "configured-mic", "resolved-wasapi-m113", signalWarning: false));

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        var runStatus = fixture.Store.GetStoredRun(runId)!.Status;
        var stepStatus = fixture.Store.GetSteps(runId)["audio"].Status;
        Assert.NotEqual(InspectionRunStatus.Pass, runStatus);
        Assert.NotEqual(InspectionRunStatus.Pass, stepStatus);
        Assert.True(InspectionStore.IsTerminal(runStatus));
        Assert.True(InspectionStore.IsTerminal(stepStatus));
        Assert.Single(session.AudioCalls);

        var output = fixture.Store.GetStepOutput(runId, "audio")!.Value;
        Assert.False(output.GetProperty("physicalPassed").GetBoolean());
        Assert.Equal("physical-path-not-verified", output.GetProperty("verdict").GetString());
        Assert.False(output.GetProperty("physicalTests")[0].GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Every_required_microphone_is_physically_tested_with_its_resolved_endpoint()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = ["configured-a", "configured-b"];
        });
        var session = new RecordingSessionAgentClient(call =>
        {
            var endpointId = call.RequiredPayload.GetProperty("captureEndpointId").GetString()!;
            return PhysicalResponse(endpointId, passed: true);
        });
        var response = AudioResponse(
            Microphone("mic:a", "configured-a", "resolved-a", signalWarning: true),
            Microphone("mic:b", "configured-b", "resolved-b", signalWarning: true));

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(
            new[] { "resolved-a", "resolved-b" },
            session.AudioCalls.Select(call =>
                call.RequiredPayload.GetProperty("captureEndpointId").GetString()!).ToArray());
        var output = fixture.Store.GetStepOutput(runId, "audio")!.Value;
        Assert.Equal(2, output.GetProperty("physicalTests").GetArrayLength());
        Assert.True(output.GetProperty("physicalPassed").GetBoolean());
    }

    [Fact]
    public async Task Successful_single_unpinned_microphone_probe_still_warns_that_the_primary_is_not_pinned()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds.Remove("m113");
        });
        var session = new RecordingSessionAgentClient(call =>
            PhysicalResponse(call.RequiredPayload.GetProperty("captureEndpointId").GetString()!, passed: true));
        var response = AudioResponse(Microphone(
            "mic:single", "configured-single", "resolved-single", signalWarning: false));

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetSteps(runId)["audio"].Status);
        Assert.Single(session.AudioCalls);
        var output = fixture.Store.GetStepOutput(runId, "audio")!.Value;
        Assert.True(output.GetProperty("physicalPassed").GetBoolean());
        Assert.False(output.GetProperty("primaryPinned").GetBoolean());
        Assert.Equal("primary-mic-not-pinned", output.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Multiple_real_microphones_without_a_required_selection_warn_without_calling_session_agent()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds.Remove("m113");
        });
        var session = new RecordingSessionAgentClient(_ =>
            throw new Xunit.Sdk.XunitException("The ambiguous microphone set must not invoke audio-self-test."));
        var response = AudioResponse(
            Microphone("mic:a", "configured-a", "resolved-a", signalWarning: false),
            Microphone("mic:b", "configured-b", "resolved-b", signalWarning: false));

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetSteps(runId)["audio"].Status);
        Assert.Empty(session.AudioCalls);
        Assert.Equal("primary-mic-not-selected",
            fixture.Store.GetStepOutput(runId, "audio")!.Value.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Loopback_only_source_warns_without_invoking_a_physical_probe()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
        });
        var session = new RecordingSessionAgentClient(_ =>
            throw new Xunit.Sdk.XunitException("A loopback source must never be physically tested as a microphone."));
        var response = AudioResponse(new
        {
            id = "mic:loopback",
            kind = "mic",
            name = "Stereo Mix (Realtek Audio)",
            deviceId = "stereo-mix",
            resolvedDeviceId = "stereo-mix",
            available = true,
            captureError = (string?)null,
            muted = false,
            gain = 1.0,
            bytesReceived = 8192,
            signalWarning = false,
            loopback = true
        });

        var runId = await RunAsync(fixture, session, response, PhysicalStep());

        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Empty(session.AudioCalls);
        Assert.Equal("loopback-only",
            fixture.Store.GetStepOutput(runId, "audio")!.Value.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Legacy_desktop_audio_physical_flag_without_endpoint_never_sends_a_payloadless_probe()
    {
        using var fixture = new StoreFixture();
        var session = new RecordingSessionAgentClient(call => call.Action switch
        {
            "monitors" => Response(new { monitors = Array.Empty<object>() }),
            "self-test" => Response(new { consoleWindowVisible = false }),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected SessionAgent action: {call.Action}")
        });
        var desktop = new InspectionStep
        {
            Id = "desktop",
            Type = InspectionStepType.DesktopProbe,
            TimeoutSeconds = 10,
            Input = JsonSerializer.SerializeToElement(new { audioPhysical = true }, InspectionProtocol.Json)
        };

        var runId = await RunAsync(fixture, session, "{}", desktop);

        Assert.Equal(InspectionRunStatus.Fail, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Fail, fixture.Store.GetSteps(runId)[desktop.Id].Status);
        Assert.DoesNotContain(session.Calls, call => call.Action == "audio-self-test");
        Assert.Equal(["monitors", "self-test"], session.Calls.Select(call => call.Action).ToArray());
    }

    [Fact]
    public void Repository_startup_plan_uses_readiness_only_then_a_bounded_physical_probe()
    {
        var repository = FindRepositoryRoot();
        var planPath = Path.Combine(repository, "inspection", "bundles", "startup-v0.4.1", "plan.json");
        var plan = JsonSerializer.Deserialize<InspectionPlan>(File.ReadAllText(planPath), InspectionProtocol.Json)!;
        var steps = plan.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);

        Assert.True(steps["microphone-path"].Input.GetProperty("readinessOnly").GetBoolean());
        Assert.False(steps["microphone-path"].Input.TryGetProperty("physical", out var initialPhysical)
            && initialPhysical.ValueKind == JsonValueKind.True);
        Assert.True(steps["final-audio"].Input.GetProperty("physical").GetBoolean());
        Assert.InRange(steps["final-audio"].Input.GetProperty("physicalTimeoutSeconds").GetInt32(), 4, 30);
        Assert.Contains("runtime-settle", steps["final-audio"].DependsOn);
        Assert.True(steps["final-audio"].TimeoutSeconds >=
            steps["final-audio"].Input.GetProperty("captureReadyTimeoutSeconds").GetInt32()
            + steps["final-audio"].Input.GetProperty("signalSettleSeconds").GetInt32()
            + steps["final-audio"].Input.GetProperty("physicalTimeoutSeconds").GetInt32());
    }

    private static StoreFixture RequiredM113Fixture(string requiredId)
    {
        var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = [requiredId];
        });
        return fixture;
    }

    private static InspectionStep Step(string id, object input) => new()
    {
        Id = id,
        Type = InspectionStepType.AudioProbe,
        TimeoutSeconds = 10,
        Input = JsonSerializer.SerializeToElement(input, InspectionProtocol.Json)
    };

    private static InspectionStep PhysicalStep() => Step("audio", new
    {
        captureReadyTimeoutSeconds = 0,
        signalSettleSeconds = 0,
        physical = true,
        physicalTimeoutSeconds = 8
    });

    private static object Microphone(
        string id,
        string deviceId,
        string? resolvedDeviceId,
        bool signalWarning) => new
    {
        id,
        kind = "mic",
        name = $"Microphone {id}",
        deviceId,
        resolvedDeviceId,
        resolvedDeviceName = resolvedDeviceId is null ? null : $"Resolved {id}",
        available = true,
        captureError = (string?)null,
        muted = false,
        gain = 1.0,
        bytesReceived = 8192,
        signalWarning,
        loopback = false
    };

    private static string AudioResponse(params object[] microphones) =>
        JsonSerializer.Serialize(new { sources = microphones }, InspectionProtocol.Json);

    private static SessionResponse PhysicalResponse(
        string resolvedEndpointId,
        bool passed,
        bool isLoopback = false,
        string? reason = null) => Response(new
        {
            passed,
            reason = reason ?? (passed ? "verified" : "tone-not-detected"),
            resolvedCaptureEndpointId = resolvedEndpointId,
            isLoopback,
            error = (string?)null
        });

    private static SessionResponse Response(object payload) => new(
        "test",
        true,
        null,
        JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json));

    private static async Task<string> RunAsync(
        StoreFixture fixture,
        ISessionAgentClient session,
        string audioResponse,
        params InspectionStep[] steps)
    {
        var bundle = new StoredBundle(
            new InspectionBundleManifest
            {
                BundleId = $"audio-physical-{Guid.NewGuid():N}",
                Version = "0.4.1",
                SigningKeyId = "test",
                Plan = new InspectionPlan { Name = "audio physical integration", Steps = [.. steps] }
            },
            fixture.Root);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            session,
            new BrokerClient(fixture.Options),
            new JsonHttpClientFactory(audioResponse),
            NullLogger<InspectionEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForTerminalAsync(fixture.Store, runId, TimeSpan.FromSeconds(8));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
        return runId;
    }

    private static async Task WaitForTerminalAsync(InspectionStore store, string runId, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (store.GetStoredRun(runId)?.Status is InspectionRunStatus.Queued or InspectionRunStatus.Running)
            await Task.Delay(25, cancellation.Token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }

    private sealed record SessionCall(string Action, JsonElement? Payload)
    {
        public JsonElement RequiredPayload => Payload
            ?? throw new Xunit.Sdk.XunitException($"{Action} was invoked without a payload.");
    }

    private sealed class RecordingSessionAgentClient(Func<SessionCall, SessionResponse> responder)
        : ISessionAgentClient
    {
        private readonly ConcurrentQueue<SessionCall> _calls = new();

        public SessionCall[] Calls => _calls.ToArray();
        public SessionCall[] AudioCalls => Calls.Where(call => call.Action == "audio-self-test").ToArray();

        public Task<SessionResponse> SendAsync(
            string action,
            object? payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new SessionCall(action, payload is null
                ? null
                : JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json));
            _calls.Enqueue(call);
            return Task.FromResult(responder(call));
        }
    }

    private sealed class JsonHttpClientFactory(string response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new JsonHandler(response));

        private sealed class JsonHandler(string response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
                });
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "SilentStreamAudioPhysicalIntegrationTests", Guid.NewGuid().ToString("N"));
        public CoordinatorOptionsStore Options { get; }
        public InspectionStore Store { get; }

        public StoreFixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CoordinatorOptionsStore(Root);
            Store = new InspectionStore(Options);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
