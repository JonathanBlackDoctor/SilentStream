using System.Text.Json;
using SilentStream.Inspection;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class StartupBundleSafetyPolicyTests
{
    [Fact]
    public void Normal_read_only_startup_plan_is_allowed()
    {
        var plan = Plan(
            Step("collect", InspectionStepType.Collect, new { }),
            Step("wait", InspectionStepType.Wait, new { seconds = 1 }),
            Step("app", InspectionStepType.AppApi,
                new { method = "GET", url = "http://127.0.0.1:8787/api/status" }),
            Step("desktop", InspectionStepType.DesktopProbe, new { monitor = 0 }),
            Step("audio", InspectionStepType.AudioProbe, new { readinessOnly = true }));

        Assert.True(StartupBundleSafetyPolicy.IsSafe(plan, out var reason), reason);
        StartupBundleSafetyPolicy.EnsureSafe(plan);
    }

    [Fact]
    public void Plan_not_marked_for_startup_is_rejected()
    {
        var plan = Plan(Step("wait", InspectionStepType.Wait, new { seconds = 1 })) with
        {
            RunAtStartup = false
        };

        Assert.False(StartupBundleSafetyPolicy.IsSafe(plan, out var reason));
        Assert.Contains("not marked", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => StartupBundleSafetyPolicy.EnsureSafe(plan));
    }

    [Theory]
    [InlineData(InspectionStepType.Script)]
    [InlineData(InspectionStepType.Privileged)]
    [InlineData(InspectionStepType.Fault)]
    [InlineData(InspectionStepType.Checkpoint)]
    [InlineData(InspectionStepType.Cleanup)]
    public void Prohibited_step_type_is_rejected(InspectionStepType type)
    {
        var plan = Plan(Step("unsafe", type, new { }));

        Assert.False(StartupBundleSafetyPolicy.IsSafe(plan, out var reason));
        Assert.Contains("prohibited", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Destructive_marker_is_rejected_even_when_nested_or_differently_cased()
    {
        var plan = Plan(Step("collect", InspectionStepType.Collect, new
        {
            payload = new { DeStRuCtIvE = true }
        }));

        Assert.False(StartupBundleSafetyPolicy.IsSafe(plan, out var reason));
        Assert.Contains("destructive", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_missing_or_non_get_app_api_is_rejected()
    {
        var cases = new (string Name, JsonElement Input)[]
        {
            ("undefined", default),
            ("non-object", JsonSerializer.SerializeToElement("GET", InspectionProtocol.Json)),
            ("missing", JsonSerializer.SerializeToElement(new
                { url = "http://127.0.0.1:8787/api/status" }, InspectionProtocol.Json)),
            ("wrong-kind", JsonSerializer.SerializeToElement(new
                { method = 42, url = "http://127.0.0.1:8787/api/status" }, InspectionProtocol.Json)),
            ("blank", JsonSerializer.SerializeToElement(new
                { method = " ", url = "http://127.0.0.1:8787/api/status" }, InspectionProtocol.Json)),
            ("mutating", JsonSerializer.SerializeToElement(new
                { method = "POST", url = "http://127.0.0.1:8787/api/status" }, InspectionProtocol.Json))
        };

        foreach (var scenario in cases)
        {
            var plan = Plan(new InspectionStep
            {
                Id = scenario.Name,
                Type = InspectionStepType.AppApi,
                Input = scenario.Input
            });
            Assert.False(StartupBundleSafetyPolicy.IsSafe(plan, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    private static InspectionPlan Plan(params InspectionStep[] steps) => new()
    {
        Name = "startup safety test",
        RunAtStartup = true,
        Steps = [.. steps]
    };

    private static InspectionStep Step(string id, InspectionStepType type, object input) => new()
    {
        Id = id,
        Type = type,
        Input = JsonSerializer.SerializeToElement(input, InspectionProtocol.Json)
    };
}
