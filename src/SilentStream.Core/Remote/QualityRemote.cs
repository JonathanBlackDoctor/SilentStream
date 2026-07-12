using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Remote;

/// <summary>
/// The phone remote's quality contract (확장계획서_적응형송출품질 §7.2): concrete DTOs +
/// request handling for GET/PUT <c>/api/quality</c>. Lives in Core (not the App endpoint
/// lambdas) so the wire shape is unit-tested — the platform-neutral test project references
/// Core only. Serialized camelCase by the remote server's JSON options.
/// </summary>
public static class QualityRemote
{
    /// <summary>One ladder rung on the wire.</summary>
    public sealed record StepDto(int Level, string Name, int Width, int Height, double Fps, int VideoBitrateKbps);

    /// <summary>GET /api/quality response body.</summary>
    /// <param name="Mode">"auto" | "manual".</param>
    /// <param name="Level">DESIRED level (controller); the applied truth is in <paramref name="Current"/>.</param>
    /// <param name="LevelName">Korean label of the desired level.</param>
    /// <param name="Base">The 원본 rung, or null before the first session built the ladder.</param>
    /// <param name="Current">APPLIED encode parameters (orchestrator truth), null before any start.</param>
    /// <param name="Ladder">All rungs of the per-session ladder (empty before the first session).</param>
    /// <param name="AdaptiveEnabled">config encoding.adaptive.enabled — gates automatic changes only.</param>
    /// <param name="DegradedSince">Local time the level first left 0, or null.</param>
    public sealed record QualityDto(
        string Mode,
        int Level,
        string LevelName,
        StepDto? Base,
        StepDto? Current,
        IReadOnlyList<StepDto> Ladder,
        bool AdaptiveEnabled,
        DateTime? DegradedSince);

    /// <summary>PUT /api/quality request body: {"mode":"auto"} or {"mode":"manual","level":2}.</summary>
    public sealed record PutRequest(string? Mode, int? Level);

    /// <summary>
    /// PUT outcome. <paramref name="Applied"/>: "swapped" when a live pipeline is being rebuilt
    /// right away, "deferred" when the level waits for the next encoder start, "none" on error.
    /// </summary>
    public sealed record PutResult(bool Ok, string? Error, string Applied, QualityDto Quality);

    /// <summary>Builds the GET body from the desired (controller) + applied (orchestrator) truths.</summary>
    public static QualityDto BuildDto(
        IAdaptiveQualityController controller, QualityStatus applied, bool adaptiveEnabled)
    {
        var desired = controller.Status;
        var ladder = controller.Ladder.Select(ToDto).ToList();
        return new QualityDto(
            Mode: desired.Mode == QualityMode.ManualHold ? "manual" : "auto",
            Level: desired.Level,
            LevelName: desired.LevelName,
            Base: ladder.Count > 0 ? ladder[0] : null,
            Current: applied.Applied is { } step ? ToDto(step) : null,
            Ladder: ladder,
            AdaptiveEnabled: adaptiveEnabled,
            DegradedSince: desired.DegradedSince);
    }

    /// <summary>
    /// Applies a PUT request to the controller. The actual pipeline swap happens asynchronously
    /// through the controller→orchestrator wiring; <paramref name="live"/> only shapes the
    /// honest "swapped"/"deferred" answer for the phone toast. <paramref name="applied"/> is the
    /// orchestrator's applied truth, echoed back in the response body.
    /// </summary>
    public static PutResult Apply(
        PutRequest? request, IAdaptiveQualityController controller, QualityStatus applied,
        bool adaptiveEnabled, bool live)
    {
        switch (request?.Mode?.Trim().ToLowerInvariant())
        {
            case "auto":
                controller.SetAuto();
                break;
            case "manual" when request.Level is int level && level >= 0:
                controller.SetManual(level);
                break;
            default:
                return new PutResult(false,
                    "mode는 \"auto\" 또는 \"manual\"(level 0 이상 포함)이어야 합니다.", "none",
                    BuildDto(controller, applied, adaptiveEnabled));
        }
        return new PutResult(true, null, live ? "swapped" : "deferred",
            BuildDto(controller, applied, adaptiveEnabled));
    }

    private static StepDto ToDto(QualityStep step) =>
        new(step.Level, step.Name, step.Width, step.Height, step.Fps, step.VideoBitrateKbps);
}
