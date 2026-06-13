using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace SilentStream.Media.Windows;

/// <summary>
/// Primary-monitor capture via DXGI Desktop Duplication (plan §3.3): full screen,
/// mouse cursor included, auto re-initialisation when access is lost (fullscreen
/// exclusive apps, resolution changes). Frames are delivered as BGRA buffers.
/// </summary>
public sealed class DxgiScreenCaptureSource : IScreenCaptureSource
{
    private readonly ILogService _log;

    private Device? _device;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;
    private Task? _captureLoop;
    private CancellationTokenSource? _cts;
    private byte[]? _frameBuffer;

    // Last known cursor shape, drawn manually: Desktop Duplication does not composite it.
    private byte[]? _cursorShape;
    private OutputDuplicatePointerShapeInformation _cursorInfo;
    private SharpDX.Mathematics.Interop.RawPoint _cursorPosition;
    private bool _cursorVisible;

    public DxgiScreenCaptureSource(ILogService log) => _log = log;

    public bool IsCapturing => _captureLoop is { IsCompleted: false };

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Fps { get; private set; } = 30;

    public event EventHandler<VideoFrame>? FrameCaptured;

    public Task StartAsync(CancellationToken ct)
    {
        if (IsCapturing)
        {
            return Task.CompletedTask;
        }

        Initialize();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _captureLoop = Task.Factory.StartNew(
            () => CaptureLoop(_cts.Token), _cts.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _log.Info($"화면 캡처 시작: 주 모니터 {Width}x{Height}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_captureLoop is not null)
        {
            try
            {
                await _captureLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        ReleaseDuplication();
        _log.Info("화면 캡처 중지");
    }

    private void Initialize()
    {
        using var factory = new Factory1();
        using var adapter = factory.GetAdapter1(0);
        _device = new Device(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);

        using var output = adapter.GetOutput(0); // primary monitor
        using var output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;
        Fps = GetPrimaryRefreshRate();

        _duplication = output1.DuplicateOutput(_device);
        _stagingTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });
        _frameBuffer = new byte[Width * Height * 4];
    }

    private void CaptureLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CaptureOneFrame(ct);
            }
            catch (SharpDXException ex) when (
                ex.ResultCode == SharpDX.DXGI.ResultCode.AccessLost ||
                ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved ||
                ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
            {
                // Fullscreen exclusive handoff or display-mode change: rebuild (plan §3.3).
                _log.Warn("캡처 세션 손실 — 재초기화합니다.");
                ReleaseDuplication();
                Thread.Sleep(500);
                try
                {
                    Initialize();
                }
                catch (SharpDXException initEx)
                {
                    _log.Error("캡처 재초기화 실패 — 1초 후 재시도", initEx);
                    Thread.Sleep(1000);
                }
            }
        }
    }

    private void CaptureOneFrame(CancellationToken ct)
    {
        var duplication = _duplication;
        var device = _device;
        var staging = _stagingTexture;
        if (duplication is null || device is null || staging is null || _frameBuffer is null)
        {
            return;
        }

        var result = duplication.TryAcquireNextFrame(100, out var frameInfo, out Resource? desktopResource);
        if (result.Failure)
        {
            if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
            {
                return; // No screen change within the wait window.
            }
            result.CheckError();
        }

        try
        {
            UpdateCursor(duplication, frameInfo);

            using (var texture = desktopResource!.QueryInterface<Texture2D>())
            {
                device.ImmediateContext.CopyResource(texture, staging);
            }

            var map = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                CopyToBuffer(map);
            }
            finally
            {
                device.ImmediateContext.UnmapSubresource(staging, 0);
            }

            DrawCursor();
            FrameCaptured?.Invoke(this, new VideoFrame(Width, Height, DateTime.UtcNow, _frameBuffer));
        }
        finally
        {
            desktopResource?.Dispose();
            duplication.ReleaseFrame();
        }
    }

    private void CopyToBuffer(DataBox map)
    {
        var rowBytes = Width * 4;
        if (map.RowPitch == rowBytes)
        {
            Utilities.Read(map.DataPointer, _frameBuffer, 0, _frameBuffer!.Length);
            return;
        }
        for (var y = 0; y < Height; y++)
        {
            Utilities.Read(map.DataPointer + y * map.RowPitch, _frameBuffer, y * rowBytes, rowBytes);
        }
    }

    private void UpdateCursor(OutputDuplication duplication, OutputDuplicateFrameInformation frameInfo)
    {
        if (frameInfo.LastMouseUpdateTime != 0)
        {
            _cursorPosition = frameInfo.PointerPosition.Position;
            _cursorVisible = frameInfo.PointerPosition.Visible;
        }

        if (frameInfo.PointerShapeBufferSize > 0)
        {
            _cursorShape = new byte[frameInfo.PointerShapeBufferSize];
            unsafe
            {
                fixed (byte* ptr = _cursorShape)
                {
                    duplication.GetFramePointerShape(
                        frameInfo.PointerShapeBufferSize, (IntPtr)ptr,
                        out _, out _cursorInfo);
                }
            }
        }
    }

    /// <summary>
    /// Blends the cached cursor shape into the frame buffer (32bpp color cursors with
    /// alpha; masked/monochrome shapes are drawn opaque as an approximation).
    /// </summary>
    private void DrawCursor()
    {
        var shape = _cursorShape;
        if (!_cursorVisible || shape is null || _frameBuffer is null)
        {
            return;
        }
        if (_cursorInfo.Type != (int)OutputDuplicatePointerShapeType.Color &&
            _cursorInfo.Type != (int)OutputDuplicatePointerShapeType.MaskedColor)
        {
            return; // Monochrome XOR cursors are rare on modern Windows; skip.
        }

        var cursorWidth = _cursorInfo.Width;
        var cursorHeight = _cursorInfo.Height;
        for (var cy = 0; cy < cursorHeight; cy++)
        {
            var screenY = _cursorPosition.Y + cy;
            if (screenY < 0 || screenY >= Height)
            {
                continue;
            }
            for (var cx = 0; cx < cursorWidth; cx++)
            {
                var screenX = _cursorPosition.X + cx;
                if (screenX < 0 || screenX >= Width)
                {
                    continue;
                }

                var src = (cy * _cursorInfo.Pitch) + cx * 4;
                var alpha = shape[src + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var dst = (screenY * Width + screenX) * 4;
                if (alpha == 255)
                {
                    _frameBuffer[dst] = shape[src];
                    _frameBuffer[dst + 1] = shape[src + 1];
                    _frameBuffer[dst + 2] = shape[src + 2];
                }
                else
                {
                    var inv = 255 - alpha;
                    _frameBuffer[dst] = (byte)((shape[src] * alpha + _frameBuffer[dst] * inv) / 255);
                    _frameBuffer[dst + 1] = (byte)((shape[src + 1] * alpha + _frameBuffer[dst + 1] * inv) / 255);
                    _frameBuffer[dst + 2] = (byte)((shape[src + 2] * alpha + _frameBuffer[dst + 2] * inv) / 255);
                }
            }
        }
    }

    /// <summary>Primary-monitor refresh rate via EnumDisplaySettings (plan §3.3: 주사율 따름).</summary>
    private static double GetPrimaryRefreshRate()
    {
        var devMode = new Devmode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Devmode>() };
        const int enumCurrentSettings = -1;
        if (EnumDisplaySettings(null, enumCurrentSettings, ref devMode) && devMode.dmDisplayFrequency > 1)
        {
            return devMode.dmDisplayFrequency;
        }
        return 30;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct Devmode
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref Devmode devMode);

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        ReleaseDuplication();
        _cts?.Dispose();
    }
}
