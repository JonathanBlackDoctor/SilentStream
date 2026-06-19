using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Provides monitor video frames (cursor included, no overlay) via DXGI Desktop Duplication,
/// with auto re-init on capture failure. The captured monitor (and an optional crop region)
/// is chosen via config; defaults to the primary monitor, full screen. See plan §3.3.
/// </summary>
public interface IScreenCaptureSource : IDisposable
{
    /// <summary>True while frames are being produced.</summary>
    bool IsCapturing { get; }

    /// <summary>Enumerates the connected monitors for the control-UI picker (OBS 대비 모니터 선택).</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Capture width in pixels (valid after StartAsync).</summary>
    int Width { get; }

    /// <summary>Capture height in pixels (valid after StartAsync).</summary>
    int Height { get; }

    /// <summary>Primary-monitor refresh rate the capture follows (valid after StartAsync).</summary>
    double Fps { get; }

    /// <summary>Raised for each captured frame.</summary>
    event EventHandler<VideoFrame> FrameCaptured;

    /// <summary>Starts capturing the primary monitor.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops capturing and releases the duplication device.</summary>
    Task StopAsync();
}
