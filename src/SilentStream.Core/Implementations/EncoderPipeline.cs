using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real GPU detection + FFmpeg tee (RTMP + mp4) lands in Phase 2.
/// </summary>
public sealed class EncoderPipeline : IEncoderPipeline
{
    public bool IsRunning => false;

#pragma warning disable CS0067 // part of the fixed contract; implemented in Phase 2
    public event EventHandler<MetricsSnapshot>? MetricsUpdated;
#pragma warning restore CS0067

    public Task StartAsync(EncoderStartOptions options, CancellationToken ct) =>
        throw new NotImplementedException("EncoderPipeline.StartAsync — Phase 2 (FFmpeg tee).");

    public Task StopAsync() =>
        throw new NotImplementedException("EncoderPipeline.StopAsync — Phase 2 (FFmpeg tee).");

    public void Dispose() { }
}
