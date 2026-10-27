namespace SilentStream.Core.Contracts;

/// <summary>Checks the server-side end and archive state; can complete an orphan from a previous process.</summary>
public interface IYouTubeReplayGateway
{
    Task<DateTimeOffset?> GetCompletedReplayEndAsync(
        string broadcastId, bool completeOrphan, CancellationToken ct);
}
