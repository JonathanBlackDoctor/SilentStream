namespace SilentStream.Core.Contracts;

/// <summary>Admission control for VOD only. Failure must never stop capture or streaming.</summary>
public interface IUploadBudget
{
    Task<UploadBudgetDecision> ReserveAsync(DateOnly recordingDate, string attemptId, CancellationToken ct);
    Task ReportPauseAsync(string kind, DateTimeOffset untilUtc, CancellationToken ct);
}

public sealed record UploadBudgetDecision(bool Allowed, DateTimeOffset RetryAtUtc, string Reason);
