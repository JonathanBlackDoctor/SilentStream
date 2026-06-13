using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Phase-0 stub. Real DXGI Desktop Duplication capture lands in Phase 2 (Windows-only).
/// </summary>
public sealed class ScreenCaptureSource : IScreenCaptureSource
{
    public bool IsCapturing => false;
    public int Width => 1920;
    public int Height => 1080;
    public double Fps => 30;

#pragma warning disable CS0067 // part of the fixed contract; implemented in Phase 2
    public event EventHandler<VideoFrame>? FrameCaptured;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) =>
        throw new NotImplementedException("ScreenCaptureSource.StartAsync — Phase 2 (DXGI).");

    public Task StopAsync() =>
        throw new NotImplementedException("ScreenCaptureSource.StopAsync — Phase 2 (DXGI).");

    public void Dispose() { }
}
