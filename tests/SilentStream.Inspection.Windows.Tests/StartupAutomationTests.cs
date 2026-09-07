using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class StartupAutomationTests
{
    [Fact]
    public void Boot_session_id_is_stable_across_service_restarts_in_the_same_boot()
    {
        var firstSample = new DateTimeOffset(2026, 8, 26, 1, 20, 12, TimeSpan.Zero);
        var firstUptime = TimeSpan.FromMinutes(17).TotalMilliseconds;
        var secondSample = firstSample.AddSeconds(19);
        var secondUptime = firstUptime + TimeSpan.FromSeconds(19).TotalMilliseconds;

        Assert.Equal(
            SystemBootSession.CreateId(firstSample, (long)firstUptime),
            SystemBootSession.CreateId(secondSample, (long)secondUptime));
    }

    [Fact]
    public void Boot_session_id_is_stable_when_clock_and_uptime_samples_straddle_a_bucket_boundary()
    {
        var boundary = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var uptime = TimeSpan.FromMinutes(10);
        var earlyWallClockSample = boundary + uptime - TimeSpan.FromMilliseconds(10);
        var lateWallClockSample = boundary + uptime + TimeSpan.FromSeconds(20) + TimeSpan.FromMilliseconds(10);

        Assert.Equal(
            SystemBootSession.CreateId(earlyWallClockSample, (long)uptime.TotalMilliseconds),
            SystemBootSession.CreateId(lateWallClockSample, (long)(uptime + TimeSpan.FromSeconds(20)).TotalMilliseconds));
    }

    [Fact]
    public void Boot_session_id_changes_for_a_distinct_boot_and_clamps_invalid_uptime()
    {
        var now = new DateTimeOffset(2026, 8, 26, 1, 30, 0, TimeSpan.Zero);
        var firstBoot = SystemBootSession.CreateId(now, (long)TimeSpan.FromMinutes(20).TotalMilliseconds);
        var laterBoot = SystemBootSession.CreateId(now.AddMinutes(2), (long)TimeSpan.FromSeconds(10).TotalMilliseconds);

        Assert.NotEqual(firstBoot, laterBoot);
        Assert.Equal(SystemBootSession.CreateId(now, 0), SystemBootSession.CreateId(now, -1));
    }

    [Fact]
    public void Later_update_poll_cannot_erase_an_inspection_snapshot_after_readiness()
    {
        var gate = new StartupUpdateGate();
        gate.MarkReady(true, "initial update check completed");
        gate.MarkInspection("inspection-queued", "M113 inspection queued", "run-m113");

        gate.MarkApplying("periodic updater poll started");
        gate.MarkReady(true, "periodic update poll completed");

        Assert.Equal("inspection-queued", gate.Snapshot.Phase);
        Assert.Equal("run-m113", gate.Snapshot.RunId);
    }

    [Fact]
    public async Task Startup_update_gate_commits_only_the_first_readiness_result()
    {
        var gate = new StartupUpdateGate();
        gate.MarkApplying("installing update");
        Assert.Equal("applying-update", gate.Snapshot.Phase);

        gate.MarkReady(false, "initial update check failed");
        var committed = gate.Snapshot;
        gate.MarkApplying("late updater poll");
        gate.MarkReady(true, "late updater result");

        var readiness = await gate.WaitAsync(CancellationToken.None);
        Assert.False(readiness.UpdateCheckSucceeded);
        Assert.Equal("initial update check failed", readiness.Message);
        Assert.Equal(committed, gate.Snapshot);
    }

    [Fact]
    public void Startup_policy_accepts_only_the_read_only_step_types()
    {
        var bundle = Bundle("safe", true,
            Step("collect", InspectionStepType.Collect, new { }),
            Step("wait", InspectionStepType.Wait, new { seconds = 1 }),
            Step("status", InspectionStepType.AppApi,
                new { method = "GET", url = "http://127.0.0.1:8787/api/status" }),
            Step("desktop", InspectionStepType.DesktopProbe, new { monitor = 0 }),
            Step("audio", InspectionStepType.AudioProbe, new { settleSeconds = 1 }));

        Assert.True(InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason), reason);
    }

    [Fact]
    public void Startup_policy_is_an_explicit_allowlist_for_all_step_types()
    {
        var allowed = new HashSet<InspectionStepType>
        {
            InspectionStepType.Collect,
            InspectionStepType.Wait,
            InspectionStepType.AppApi,
            InspectionStepType.DesktopProbe,
            InspectionStepType.AudioProbe
        };

        foreach (var type in Enum.GetValues<InspectionStepType>())
        {
            var input = type == InspectionStepType.AppApi
                ? new { method = "GET", url = "http://127.0.0.1:8787/api/status" }
                : (object)new { };
            var accepted = InspectionApiPolicy.IsSafeStartupBundle(
                Bundle($"allowlist-{type}", true, Step("candidate", type, input)), out _);

            Assert.Equal(allowed.Contains(type), accepted);
        }
    }

    [Theory]
    [InlineData(InspectionStepType.Script)]
    [InlineData(InspectionStepType.Privileged)]
    [InlineData(InspectionStepType.Fault)]
    [InlineData(InspectionStepType.Checkpoint)]
    [InlineData(InspectionStepType.Cleanup)]
    public void Startup_policy_rejects_every_privileged_or_interactive_step_type(InspectionStepType type)
    {
        var bundle = Bundle("unsafe-type", true, Step("unsafe", type, new { action = "reboot" }));

        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason));
        Assert.Contains("prohibited", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Startup_policy_rejects_mutating_application_requests(string method)
    {
        var bundle = Bundle("unsafe-http", true,
            Step("mutate", InspectionStepType.AppApi,
                new { method, url = "http://127.0.0.1:8787/api/live/start" }));

        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason));
        Assert.Contains("mutating", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_policy_rejects_a_malformed_application_method_without_throwing()
    {
        var bundle = Bundle("malformed-http", true,
            Step("malformed", InspectionStepType.AppApi,
                new { method = 42, url = "http://127.0.0.1:8787/api/status" }));

        var exception = Record.Exception(() => InspectionApiPolicy.IsSafeStartupBundle(bundle, out _));

        Assert.Null(exception);
        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason));
        Assert.Contains("method", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_policy_rejects_destructive_marker_even_on_an_otherwise_safe_step()
    {
        var bundle = Bundle("unsafe-marker", true,
            Step("collect", InspectionStepType.Collect, new { destructive = true }));

        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(bundle, out var reason));
        Assert.Contains("destructive", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_startup_plan_is_safe_but_full_maintenance_plan_is_never_automatic()
    {
        var repository = FindRepositoryRoot();
        var startup = ReadPlan(Path.Combine(repository, "inspection", "bundles", "startup-v0.4.2", "plan.json"));
        var full = ReadPlan(Path.Combine(repository, "inspection", "bundles", "full-v0.4.0", "plan.json"));

        Assert.True(InspectionApiPolicy.IsSafeStartupBundle(Bundle("startup-template", "0.4.2", startup), out var startupReason), startupReason);
        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(Bundle("full-template", "0.4.0", full), out var fullReason));
        Assert.Contains("not marked", fullReason, StringComparison.OrdinalIgnoreCase);

        var accidentallyMarkedFull = full with { RunAtStartup = true };
        Assert.False(InspectionApiPolicy.IsSafeStartupBundle(
            Bundle("full-template-marked", "0.4.0", accidentallyMarkedFull), out var destructiveReason));
        Assert.Contains("prohibited", destructiveReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_startup_plan_waits_for_application_readiness_and_audio_runtime_settle()
    {
        var repository = FindRepositoryRoot();
        var plan = ReadPlan(Path.Combine(repository, "inspection", "bundles", "startup-v0.4.2", "plan.json"));
        var steps = plan.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);

        var initialWait = steps["wait-for-app"];
        var initialWaitSeconds = InputInt(initialWait, "seconds");
        Assert.InRange(initialWaitSeconds, 1, 10);
        Assert.True(initialWait.TimeoutSeconds >= initialWaitSeconds);

        var appStatus = steps["app-status"];
        var readySeconds = InputInt(appStatus, "waitForReadySeconds");
        Assert.Contains(initialWait.Id, appStatus.DependsOn);
        Assert.InRange(readySeconds, 120, 300);
        Assert.True(appStatus.TimeoutSeconds >= readySeconds);
        Assert.Contains(appStatus.Id, steps["desktop-capture"].DependsOn);

        var microphone = steps["microphone-path"];
        var microphoneReadyTimeout = InputInt(microphone, "captureReadyTimeoutSeconds");
        var microphoneSignalSettle = InputInt(microphone, "signalSettleSeconds");
        Assert.Contains(appStatus.Id, microphone.DependsOn);
        Assert.InRange(microphoneReadyTimeout, 30, 180);
        Assert.InRange(microphoneSignalSettle, 10, 30);
        Assert.True(microphone.TimeoutSeconds >= microphoneReadyTimeout + microphoneSignalSettle);

        var runtimeSettle = steps["runtime-settle"];
        var runtimeSettleSeconds = InputInt(runtimeSettle, "seconds");
        Assert.Contains(microphone.Id, runtimeSettle.DependsOn);
        Assert.Contains("desktop-capture", runtimeSettle.DependsOn);
        Assert.InRange(runtimeSettleSeconds, 10, 60);

        var finalAudio = steps["final-audio"];
        var finalReadyTimeout = InputInt(finalAudio, "captureReadyTimeoutSeconds");
        var finalSignalSettle = InputInt(finalAudio, "signalSettleSeconds");
        Assert.Contains(runtimeSettle.Id, finalAudio.DependsOn);
        Assert.InRange(finalReadyTimeout, 30, 180);
        Assert.InRange(finalSignalSettle, 10, 30);
        Assert.True(finalAudio.TimeoutSeconds >= finalReadyTimeout + finalSignalSettle);
        Assert.True(microphoneSignalSettle + runtimeSettleSeconds + finalSignalSettle >= 30,
            "Startup audio checks need enough post-readiness time to receive real capture packets.");
    }

    [Fact]
    public void Startup_run_is_created_only_once_per_boot_even_after_store_reopens()
    {
        using var fixture = new StoreFixture();
        var bundle = Bundle("startup-once", true,
            Step("wait", InspectionStepType.Wait, new { seconds = 1 }));
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));

        Assert.True(fixture.Store.TryCreateStartupRun(bundle, "boot-a", out var firstRun));
        Assert.False(fixture.Store.TryCreateStartupRun(bundle, "boot-a", out var duplicateRun));
        Assert.Equal(firstRun, duplicateRun);

        var reopened = new InspectionStore(fixture.Options);
        Assert.False(reopened.TryCreateStartupRun(bundle, "boot-a", out var reopenedRun));
        Assert.Equal(firstRun, reopenedRun);
        Assert.Single(reopened.ListRuns());

        Assert.True(reopened.TryCreateStartupRun(bundle, "boot-b", out var nextBootRun));
        Assert.NotEqual(firstRun, nextBootRun);
        Assert.Equal(2, reopened.ListRuns().Count);
    }

    [Fact]
    public async Task Startup_inspection_waits_for_the_update_gate_before_queueing_a_run()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M111";
            options.RoomId = "m111";
            options.StartupInspectionDelaySeconds = 0;
        });
        var bundle = Bundle("gated-startup", true,
            Step("wait", InspectionStepType.Wait, new { seconds = 1 }));
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var gate = new StartupUpdateGate();
        using var service = new StartupInspectionHostedService(
            fixture.Options, fixture.Store, gate, NullLogger<StartupInspectionHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.Empty(fixture.Store.ListRuns());

            gate.MarkReady(true, "update completed");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (gate.Snapshot.Phase != "inspection-queued")
                await Task.Delay(25, timeout.Token);

            Assert.Single(fixture.Store.ListRuns());
            Assert.Equal("inspection-queued", gate.Snapshot.Phase);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Required_warning_is_terminal_allows_dependents_and_finishes_run_as_warning()
    {
        using var fixture = new StoreFixture();
        var warning = Step("warning-source", InspectionStepType.Collect, new { });
        var dependent = Step("after-warning", InspectionStepType.Wait, new { seconds = 1 }) with
        {
            DependsOn = [warning.Id],
            TimeoutSeconds = 5
        };
        var bundle = Bundle("warning-flow", false, warning, dependent);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);
        fixture.Store.UpdateStep(runId, warning.Id, InspectionRunStatus.Warning, 1, "operator attention");

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            new SessionAgentClient(),
            new BrokerClient(fixture.Options),
            new NoopHttpClientFactory(),
            NullLogger<InspectionEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.Store.GetStoredRun(runId)?.Status is InspectionRunStatus.Queued or InspectionRunStatus.Running)
                await Task.Delay(50, timeout.Token);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        Assert.True(InspectionStore.IsTerminal(InspectionRunStatus.Warning));
        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetSteps(runId)[dependent.Id].Status);
        var progress = fixture.Store.GetRunProgress(runId)!;
        Assert.Equal(2, progress.TerminalSteps);
        Assert.Contains(progress.Issues, issue => issue.Contains(warning.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audio_probe_uses_resolved_loopback_status_and_never_passes_it_as_a_physical_microphone()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
        });
        var probe = Step("microphone-path", InspectionStepType.AudioProbe,
            new { captureReadyTimeoutSeconds = 0, signalSettleSeconds = 0 });
        var bundle = Bundle("loopback-only-audio", false, probe);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);
        const string audioResponse = """
            {
              "micWarning": false,
              "sources": [
                {
                  "id": "mic:default",
                  "kind": "mic",
                  "name": "Default capture endpoint",
                  "deviceId": null,
                  "available": true,
                  "captureError": null,
                  "muted": false,
                  "gain": 1.0,
                  "bytesReceived": 8192,
                  "loopback": true,
                  "resolvedDeviceId": "stereo-mix-endpoint",
                  "resolvedDeviceName": "Stereo Mix (Realtek Audio)"
                }
              ]
            }
            """;

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            new SessionAgentClient(),
            new BrokerClient(fixture.Options),
            new JsonHttpClientFactory(audioResponse),
            NullLogger<InspectionEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForTerminalAsync(fixture.Store, runId, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Warning, fixture.Store.GetSteps(runId)[probe.Id].Status);
        var output = fixture.Store.GetStepOutput(runId, probe.Id)!.Value;
        Assert.Equal("loopback-only", output.GetProperty("verdict").GetString());
        Assert.True(output.GetProperty("microphones")[0].GetProperty("loopback").GetBoolean());
    }

    [Fact]
    public async Task Audio_probe_matches_a_required_physical_mic_by_the_resolved_default_endpoint_id()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = ["physical-m113-endpoint"];
        });
        var probe = Step("microphone-path", InspectionStepType.AudioProbe,
            new { captureReadyTimeoutSeconds = 0, signalSettleSeconds = 0 });
        var bundle = Bundle("resolved-physical-audio", false, probe);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);
        const string audioResponse = """
            {
              "micWarning": false,
              "sources": [
                {
                  "id": "mic:default",
                  "kind": "mic",
                  "name": "Default capture endpoint",
                  "deviceId": null,
                  "available": true,
                  "captureError": null,
                  "muted": false,
                  "gain": 1.0,
                  "bytesReceived": 8192,
                  "signalWarning": false,
                  "loopback": false,
                  "resolvedDeviceId": "physical-m113-endpoint",
                  "resolvedDeviceName": "M113 Wireless Microphone Receiver"
                }
              ]
            }
            """;

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            new SessionAgentClient(),
            new BrokerClient(fixture.Options),
            new JsonHttpClientFactory(audioResponse),
            NullLogger<InspectionEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForTerminalAsync(fixture.Store, runId, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetSteps(runId)[probe.Id].Status);
        var output = fixture.Store.GetStepOutput(runId, probe.Id)!.Value;
        Assert.Equal("healthy", output.GetProperty("verdict").GetString());
        Assert.Equal("physical-m113-endpoint", output.GetProperty("requiredIds")[0].GetString());
        Assert.Equal("physical-m113-endpoint",
            output.GetProperty("microphones")[0].GetProperty("resolvedDeviceId").GetString());
    }

    [Fact]
    public async Task Audio_probe_waits_for_a_transient_capture_error_and_passes_after_the_required_mic_recovers()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = ["m113-xaudio"];
        });
        var probe = Step("microphone-path", InspectionStepType.AudioProbe,
            new { captureReadyTimeoutSeconds = 5, signalSettleSeconds = 0 });
        var bundle = Bundle("recovering-required-audio", false, probe);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);
        const string unavailableResponse = """
            {
              "sources": [
                {
                  "id": "mic:m113-xaudio",
                  "kind": "mic",
                  "name": "M113 xAudio microphone",
                  "deviceId": "m113-xaudio",
                  "available": false,
                  "captureError": "USB receiver is still enumerating",
                  "muted": false,
                  "gain": 1.0,
                  "bytesReceived": 0,
                  "signalWarning": true,
                  "loopback": false
                }
              ]
            }
            """;
        const string recoveredResponse = """
            {
              "sources": [
                {
                  "id": "mic:m113-xaudio",
                  "kind": "mic",
                  "name": "M113 xAudio microphone",
                  "deviceId": "m113-xaudio",
                  "available": true,
                  "captureError": null,
                  "muted": false,
                  "gain": 1.0,
                  "bytesReceived": 8192,
                  "signalWarning": false,
                  "loopback": false,
                  "resolvedDeviceId": "m113-xaudio",
                  "resolvedDeviceName": "Microphone (xAudio)"
                }
              ]
            }
            """;
        var http = new JsonHttpClientFactory(unavailableResponse, recoveredResponse);

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            new SessionAgentClient(),
            new BrokerClient(fixture.Options),
            http,
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

        Assert.True(http.RequestCount >= 2);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetStoredRun(runId)!.Status);
        Assert.Equal(InspectionRunStatus.Pass, fixture.Store.GetSteps(runId)[probe.Id].Status);
        Assert.Equal("healthy", fixture.Store.GetStepOutput(runId, probe.Id)!.Value.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Audio_probe_fails_when_any_required_microphone_is_muted_even_if_another_is_healthy()
    {
        using var fixture = new StoreFixture();
        fixture.Options.Update(options =>
        {
            options.Room = "M113";
            options.RoomId = "m113";
            options.RequiredMicrophoneDeviceIds["m113"] = ["m113-xaudio", "m113-backup"];
        });
        var probe = Step("microphone-path", InspectionStepType.AudioProbe,
            new { captureReadyTimeoutSeconds = 0, signalSettleSeconds = 0 });
        var bundle = Bundle("muted-required-audio", false, probe);
        fixture.Store.SaveBundle(new VerifiedInspectionBundle(bundle.Manifest, fixture.Root));
        var runId = fixture.Store.CreateRun(bundle, "test", false);
        const string audioResponse = """
            {
              "sources": [
                {
                  "id": "mic:m113-xaudio",
                  "kind": "mic",
                  "name": "M113 xAudio microphone",
                  "deviceId": "m113-xaudio",
                  "available": true,
                  "captureError": null,
                  "muted": false,
                  "gain": 1.0,
                  "bytesReceived": 8192,
                  "signalWarning": false,
                  "loopback": false
                },
                {
                  "id": "mic:m113-backup",
                  "kind": "mic",
                  "name": "M113 backup microphone",
                  "deviceId": "m113-backup",
                  "available": true,
                  "captureError": null,
                  "muted": true,
                  "gain": 1.0,
                  "bytesReceived": 8192,
                  "signalWarning": false,
                  "loopback": false
                }
              ]
            }
            """;

        using var engine = new InspectionEngine(
            fixture.Store,
            fixture.Options,
            new SessionAgentClient(),
            new BrokerClient(fixture.Options),
            new JsonHttpClientFactory(audioResponse),
            NullLogger<InspectionEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForTerminalAsync(fixture.Store, runId, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        Assert.Equal(InspectionRunStatus.Fail, fixture.Store.GetStoredRun(runId)!.Status);
        var state = fixture.Store.GetSteps(runId)[probe.Id];
        Assert.Equal(InspectionRunStatus.Fail, state.Status);
        Assert.Contains("필수 마이크", state.Message, StringComparison.Ordinal);
        Assert.Contains("M113 backup microphone", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Korean_baek_room_label_normalizes_to_the_ascii_backend_id()
    {
        Assert.Equal("baek202", StartupInspectionHostedService.NormalizeRoomId("백202"));
        Assert.Equal("baek202", StartupInspectionHostedService.NormalizeRoomId("백202호"));
        Assert.Equal("baek202", StartupInspectionHostedService.NormalizeRoomId("baek202"));
    }

    [Fact]
    public void Managed_room_defaults_and_provisioning_example_are_exactly_the_four_independent_rooms()
    {
        var expectedIds = new[] { "m111", "m112", "m113", "baek202" };
        var expectedNames = new[] { "M111", "M112", "M113", "백202" };
        var defaults = new CoordinatorOptions();
        Assert.Equal(expectedIds, defaults.ManagedRooms);
        Assert.True(defaults.StartupInspectionEnabled);

        var path = Path.Combine(FindRepositoryRoot(), "src", "SilentStream.ProvisioningServer", "data", "rooms.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rooms = document.RootElement.GetProperty("rooms").EnumerateArray().ToArray();

        Assert.Equal(expectedIds, rooms.Select(room => room.GetProperty("id").GetString()));
        Assert.Equal(expectedNames, rooms.Select(room => room.GetProperty("displayName").GetString()));
        Assert.Equal(4, rooms.Select(room => room.GetProperty("cloudflareHostname").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(4, rooms.Select(room => room.GetProperty("cloudflareTunnelToken").GetString()).Distinct(StringComparer.Ordinal).Count());

        var remoteUi = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "SilentStream.App", "Remote", "index.html"));
        Assert.Contains(
            "const PRIMARY_ROOMS = [\"M111\", \"M112\", \"M113\", \"백202\"]",
            remoteUi,
            StringComparison.Ordinal);

        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "inspection", "deployment", "Install-InspectionServices.ps1"));
        Assert.Contains(
            "managedRooms=@('m111','m112','m113','baek202')",
            installer,
            StringComparison.Ordinal);
    }

    private static InspectionStep Step(string id, InspectionStepType type, object input) => new()
    {
        Id = id,
        Type = type,
        Input = JsonSerializer.SerializeToElement(input, InspectionProtocol.Json)
    };

    private static StoredBundle Bundle(string id, bool runAtStartup, params InspectionStep[] steps) =>
        Bundle(id, "0.4.2", new InspectionPlan { Name = id, RunAtStartup = runAtStartup, Steps = [.. steps] });

    private static StoredBundle Bundle(string id, string version, InspectionPlan plan) => new(
        new InspectionBundleManifest
        {
            BundleId = id,
            Version = version,
            SigningKeyId = "test",
            Plan = plan
        },
        Path.Combine(Path.GetTempPath(), id));

    private static InspectionPlan ReadPlan(string path) =>
        JsonSerializer.Deserialize<InspectionPlan>(File.ReadAllText(path), InspectionProtocol.Json)
        ?? throw new InvalidDataException($"Plan is empty: {path}");

    private static int InputInt(InspectionStep step, string property) =>
        step.Input.GetProperty(property).GetInt32();

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
        return directory?.FullName ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SilentStreamStartupTests", Guid.NewGuid().ToString("N"));
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

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class JsonHttpClientFactory(params string[] responses) : IHttpClientFactory
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpClient CreateClient(string name) => new(new JsonHandler(this));

        private string NextResponse()
        {
            var index = Interlocked.Increment(ref _requestCount) - 1;
            return responses[Math.Min(index, responses.Length - 1)];
        }

        private sealed class JsonHandler(JsonHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(owner.NextResponse(), System.Text.Encoding.UTF8, "application/json")
                });
        }
    }
}
