using System.Text.Json;
using SilentStream.Inspection;
using Xunit;

namespace SilentStream.Inspection.Windows.Tests;

public sealed class OperatorCliStartupValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "silentstream-cli-startup-tests-" + Guid.NewGuid().ToString("N"));

    public OperatorCliStartupValidationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Deployment_source_validation_accepts_only_a_current_safe_startup_plan()
    {
        var source = WritePlan(SafePlan());

        var plan = global::OperatorCliStartupValidation.ValidateDeploymentSource(source, InspectionProtocol.AgentVersion);

        Assert.True(plan.RunAtStartup);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void Deployment_source_validation_rejects_non_startup_or_mutating_plans_before_creating_any_output()
    {
        var source = WritePlan(SafePlan() with { RunAtStartup = false });
        var marker = Path.Combine(_root, "must-not-exist.zip");

        Assert.Throws<InvalidDataException>(() =>
            global::OperatorCliStartupValidation.ValidateDeploymentSource(source, InspectionProtocol.AgentVersion));
        Assert.False(File.Exists(marker));

        WritePlan(SafePlan() with
        {
            Steps =
            [
                new InspectionStep
                {
                    Id = "mutate",
                    Type = InspectionStepType.AppApi,
                    Input = JsonSerializer.SerializeToElement(new { method = "POST" }, InspectionProtocol.Json)
                }
            ]
        });
        Assert.Throws<InvalidDataException>(() =>
            global::OperatorCliStartupValidation.ValidateDeploymentSource(source, InspectionProtocol.AgentVersion));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Deployment_source_validation_rejects_prohibited_and_malformed_app_api_steps()
    {
        var unsafePlans = new[]
        {
            SafePlan() with
            {
                Steps =
                [
                    new InspectionStep
                    {
                        Id = "script",
                        Type = InspectionStepType.Script,
                        Input = JsonSerializer.SerializeToElement(new { path = "unsafe.ps1" }, InspectionProtocol.Json)
                    }
                ]
            },
            SafePlan() with
            {
                Steps =
                [
                    new InspectionStep
                    {
                        Id = "null-api",
                        Type = InspectionStepType.AppApi,
                        Input = JsonSerializer.SerializeToElement<object?>(null, InspectionProtocol.Json)
                    }
                ]
            },
            SafePlan() with
            {
                Steps =
                [
                    new InspectionStep
                    {
                        Id = "missing-method",
                        Type = InspectionStepType.AppApi,
                        Input = JsonSerializer.SerializeToElement(new { url = "/api/status" }, InspectionProtocol.Json)
                    }
                ]
            },
            SafePlan() with
            {
                Steps =
                [
                    new InspectionStep
                    {
                        Id = "invalid-method",
                        Type = InspectionStepType.AppApi,
                        Input = JsonSerializer.SerializeToElement(new { method = 42 }, InspectionProtocol.Json)
                    }
                ]
            }
        };

        foreach (var unsafePlan in unsafePlans)
        {
            var source = WritePlan(unsafePlan);
            Assert.Throws<InvalidDataException>(() =>
                global::OperatorCliStartupValidation.ValidateDeploymentSource(
                    source,
                    InspectionProtocol.AgentVersion));
        }
    }

    [Fact]
    public void Deployment_and_verified_bundle_versions_must_exactly_match_the_current_agent()
    {
        var source = WritePlan(SafePlan());
        var current = Version.Parse(InspectionProtocol.AgentVersion);
        var different = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build) + 1}";

        var sourceError = Assert.Throws<InvalidDataException>(() =>
            global::OperatorCliStartupValidation.ValidateDeploymentSource(source, different));
        Assert.Contains(InspectionProtocol.AgentVersion, sourceError.Message, StringComparison.Ordinal);

        var manifest = new InspectionBundleManifest { Version = different, Plan = SafePlan() };
        var manifestError = Assert.Throws<InvalidDataException>(() =>
            global::OperatorCliStartupValidation.ValidateVerifiedBundle(manifest));
        Assert.Contains(InspectionProtocol.AgentVersion, manifestError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verified_startup_bundle_validation_applies_the_common_policy()
    {
        var safe = new InspectionBundleManifest { Version = InspectionProtocol.AgentVersion, Plan = SafePlan() };
        global::OperatorCliStartupValidation.ValidateVerifiedBundle(safe);

        var missingMethod = safe with
        {
            Plan = SafePlan() with
            {
                Steps =
                [
                    new InspectionStep
                    {
                        Id = "api",
                        Type = InspectionStepType.AppApi,
                        Input = JsonSerializer.SerializeToElement(new { url = "/api/status" }, InspectionProtocol.Json)
                    }
                ]
            }
        };
        Assert.Throws<InvalidDataException>(() =>
            global::OperatorCliStartupValidation.ValidateVerifiedBundle(missingMethod));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string WritePlan(InspectionPlan plan)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plan.json"), JsonSerializer.Serialize(plan, InspectionProtocol.Json));
        return source;
    }

    private static InspectionPlan SafePlan() => new()
    {
        Name = "startup-test",
        RunAtStartup = true,
        Steps =
        [
            new InspectionStep
            {
                Id = "wait",
                Type = InspectionStepType.Wait,
                Input = JsonSerializer.SerializeToElement(new { seconds = 1 }, InspectionProtocol.Json)
            }
        ]
    };
}
