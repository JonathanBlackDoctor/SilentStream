using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real state-machine, retries and component lifecycle land in Phase 5.
/// </summary>
public sealed class StreamOrchestrator : IStreamOrchestrator
{
    public StreamState State { get; private set; } = StreamState.Idle;

#pragma warning disable CS0067 // events are part of the fixed contract; wired up in later phases
    public event EventHandler<StreamState>? StateChanged;
    public event EventHandler<MetricsSnapshot>? MetricsUpdated;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) =>
        throw new NotImplementedException("StreamOrchestrator.StartAsync — Phase 5.");

    public Task StopAsync() =>
        throw new NotImplementedException("StreamOrchestrator.StopAsync — Phase 5.");
}
