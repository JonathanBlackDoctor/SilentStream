using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Provides primary-monitor video frames (cursor included, no overlay) via DXGI Desktop
/// Duplication, with auto re-init on capture failure. See plan §3.3.
/// </summary>
public interface IScreenCaptureSource : IDisposable
{
    /// <summary>True while frames are being produced.</summary>
    bool IsCapturing { get; }

    /// <summary>Raised for each captured frame.</summary>
    event EventHandler<VideoFrame> FrameCaptured;

    /// <summary>Starts capturing the primary monitor.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops capturing and releases the duplication device.</summary>
    Task StopAsync();
}
