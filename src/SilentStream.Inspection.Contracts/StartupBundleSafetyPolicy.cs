using System.Text.Json;

namespace SilentStream.Inspection;

/// <summary>
/// Defines the complete set of operations that a signed inspection plan may run automatically at Windows startup.
/// Keep this policy in the contracts assembly so every bundle entry point applies the same rules.
/// </summary>
public static class StartupBundleSafetyPolicy
{
    public static bool IsSafe(InspectionPlan? plan, out string reason)
    {
        if (plan is null || !plan.RunAtStartup)
        {
            reason = "The bundle is not marked for startup execution.";
            return false;
        }
        if (plan.Steps is null || plan.Steps.Count == 0)
        {
            reason = "The startup bundle has no steps.";
            return false;
        }

        foreach (var step in plan.Steps)
        {
            if (step is null)
            {
                reason = "The startup bundle contains an invalid step.";
                return false;
            }
            if (step.Type is not (InspectionStepType.Collect or InspectionStepType.Wait or InspectionStepType.AppApi
                or InspectionStepType.DesktopProbe or InspectionStepType.AudioProbe))
            {
                reason = $"Startup step '{step.Id}' uses prohibited type {step.Type}.";
                return false;
            }
            if (ContainsDestructiveMarker(step.Input))
            {
                reason = $"Startup step '{step.Id}' is marked destructive.";
                return false;
            }
            if (step.Type == InspectionStepType.AppApi
                && !IsReadOnlyAppApiRequest(step.Input, step.Id, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public static void EnsureSafe(InspectionPlan? plan)
    {
        if (!IsSafe(plan, out var reason)) throw new InvalidDataException(reason);
    }

    private static bool IsReadOnlyAppApiRequest(JsonElement input, string stepId, out string reason)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(method.GetString()))
        {
            reason = $"Startup step '{stepId}' has a missing or invalid application API method.";
            return false;
        }
        if (!string.Equals(method.GetString(), "GET", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Startup step '{stepId}' uses a mutating application API method.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ContainsDestructiveMarker(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, "destructive", StringComparison.OrdinalIgnoreCase)
                    || ContainsDestructiveMarker(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ContainsDestructiveMarker(item)) return true;
            }
        }

        return false;
    }
}
