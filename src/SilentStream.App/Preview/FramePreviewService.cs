using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.App.Preview;

/// <summary>
/// Taps the capture frames and produces a low-rate, downscaled JPEG snapshot for the control
/// window preview and the phone thumbnail (OBS 대비 송출 미리보기). Throttled to ~2 fps with at
/// most one encode in flight, and the heavy BGRA→JPEG work runs off the capture thread so it never
/// slows the encoder feed.
/// </summary>
public sealed class FramePreviewService : IPreviewProvider, IDisposable
{
    private const int TargetWidth = 480;       // thumbnail width; height keeps the source aspect
    private const long MinIntervalMs = 450;    // ~2 fps
    private const int JpegQuality = 55;

    private readonly IScreenCaptureSource _capture;
    private long _lastTicks;
    private int _encoding; // 0 = idle, 1 = an encode is in flight
    private volatile byte[]? _latestJpeg;

    public FramePreviewService(IScreenCaptureSource capture)
    {
        _capture = capture;
        _capture.FrameCaptured += OnFrameCaptured;
    }

    public event Action? FrameUpdated;

    public byte[]? GetLatestJpegFrame() => _latestJpeg;

    private void OnFrameCaptured(object? sender, VideoFrame frame)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTicks) < MinIntervalMs)
        {
            return;
        }
        // Skip if a previous encode is still running so frames never queue up.
        if (Interlocked.Exchange(ref _encoding, 1) == 1)
        {
            return;
        }
        Interlocked.Exchange(ref _lastTicks, now);

        // Copy off the capture's reused buffer before handing it to a background encode.
        var width = frame.Width;
        var height = frame.Height;
        var bgra = frame.Data.ToArray();
        Task.Run(() =>
        {
            try
            {
                Encode(bgra, width, height);
            }
            catch
            {
                // A transient imaging failure must never crash the capture/encode path.
            }
            finally
            {
                Interlocked.Exchange(ref _encoding, 0);
            }
        });
    }

    private void Encode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return;
        }

        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        source.Freeze();

        BitmapSource image = source;
        var scale = TargetWidth / (double)width;
        if (scale < 1.0)
        {
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            var scaled = new TransformedBitmap(source, transform);
            scaled.Freeze();
            image = scaled;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        _latestJpeg = ms.ToArray();

        FrameUpdated?.Invoke();
    }

    public void Dispose() => _capture.FrameCaptured -= OnFrameCaptured;
}
