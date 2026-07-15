using System.Diagnostics;
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
/// Monitor capture via DXGI Desktop Duplication (plan §3.3): the selected monitor (config
/// capture.monitorIndex; default = primary), optionally cropped to a region, mouse cursor
/// included, with auto re-initialisation when access is lost (fullscreen exclusive apps,
/// resolution changes). Frames are delivered as BGRA buffers (OBS 대비 모니터/영역 선택).
/// </summary>
public sealed class DxgiScreenCaptureSource : IScreenCaptureSource
{
    private readonly ILogService _log;
    private readonly IConfigStore _configStore;

    // YouTube live ingest only accepts standard frame rates (1440p ≤ 60fps); a monitor's
    // native refresh (e.g. 75Hz) is rejected and the connection is aborted (-10053). 30fps
    // suits lecture/screen content and keeps the live + VOD uplink light (plan §4.3).
    private const double MaxStreamFps = 30;

    private Device? _device;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;
    private Task? _captureLoop;
    private CancellationTokenSource? _cts;
    private byte[]? _frameBuffer;

    // The full monitor dimensions (staging size) and the crop offset into it. Width/Height are
    // the emitted (possibly cropped) frame size.
    private int _monitorWidth;
    private int _monitorHeight;
    private int _cropX;
    private int _cropY;

    // Last known cursor shape, drawn manually: Desktop Duplication does not composite it.
    private byte[]? _cursorShape;
    private OutputDuplicatePointerShapeInformation _cursorInfo;
    private SharpDX.Mathematics.Interop.RawPoint _cursorPosition;
    private bool _cursorVisible;

    public DxgiScreenCaptureSource(ILogService log, IConfigStore configStore)
    {
        _log = log;
        _configStore = configStore;
    }

    public bool IsCapturing => _captureLoop is { IsCompleted: false };

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Fps { get; private set; } = 30;

    public event EventHandler<VideoFrame>? FrameCaptured;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        try
        {
            using var factory = new Factory1();
            var index = 0;
            foreach (var (_, _, name, width, height, primary) in EnumerateAttachedOutputs(factory))
            {
                monitors.Add(new MonitorInfo(index++, name, width, height, primary));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"모니터 목록 조회 실패: {ex.Message}");
        }
        return monitors;
    }

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
        _log.Info($"화면 캡처 시작: {Width}x{Height} (모니터 {_monitorWidth}x{_monitorHeight}" +
                  (Width == _monitorWidth && Height == _monitorHeight ? ")" : $", 영역 {_cropX},{_cropY})"));
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
        var capture = _configStore.Load().Capture;

        using var factory = new Factory1();
        var outputs = EnumerateAttachedOutputs(factory);
        if (outputs.Count == 0)
        {
            throw new InvalidOperationException("캡처 가능한 모니터를 찾지 못했습니다.");
        }

        var index = Math.Clamp(capture.MonitorIndex, 0, outputs.Count - 1);
        var (adapterIndex, outputIndex, deviceName, _, _, _) = outputs[index];

        using var adapter = factory.GetAdapter1(adapterIndex);
        _device = new Device(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);

        using var output = adapter.GetOutput(outputIndex);
        using var output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        _monitorWidth = bounds.Right - bounds.Left;
        _monitorHeight = bounds.Bottom - bounds.Top;

        ApplyRegion(capture);
        Fps = Math.Min(GetRefreshRate(deviceName), MaxStreamFps);

        _duplication = output1.DuplicateOutput(_device);
        _stagingTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _monitorWidth,
            Height = _monitorHeight,
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

    /// <summary>
    /// Resolves the emitted frame size from the optional crop region, clamped inside the monitor
    /// and rounded to even dimensions (required by yuv420p). No region → the whole monitor.
    /// </summary>
    private void ApplyRegion(CaptureConfig capture)
    {
        var region = new CaptureRegion(capture.RegionX, capture.RegionY, capture.RegionWidth, capture.RegionHeight);
        if (region.IsEmpty)
        {
            _cropX = 0;
            _cropY = 0;
            Width = _monitorWidth;
            Height = _monitorHeight;
        }
        else
        {
            _cropX = Math.Clamp(region.X, 0, Math.Max(0, _monitorWidth - 2));
            _cropY = Math.Clamp(region.Y, 0, Math.Max(0, _monitorHeight - 2));
            Width = Math.Clamp(region.Width, 2, _monitorWidth - _cropX);
            Height = Math.Clamp(region.Height, 2, _monitorHeight - _cropY);
        }

        Width -= Width % 2;
        Height -= Height % 2;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        // Pace to a constant frame rate, emitting the last frame when the desktop is unchanged.
        // Desktop Duplication only yields a frame on change, so a static screen would otherwise
        // starve the encoder — the live stream stalls (YouTube aborts the ingest, -10053) and
        // the tee back-pressures the local recording too. Constant CFR keeps both healthy.
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, Fps));
        var clock = Stopwatch.StartNew();
        var next = interval;

        while (!ct.IsCancellationRequested)
        {
            // Initialize() can fail transiently while Windows changes display topology. When that
            // happens the prior implementation left the loop alive with null DXGI resources and
            // CaptureOneFrame simply returned forever. Retry until frames can be produced again.
            if (_device is null || _duplication is null || _stagingTexture is null || _frameBuffer is null)
            {
                if (!CaptureRecoveryPolicy.TryInitialize(ReleaseDuplication, Initialize, out var initError))
                {
                    _log.Error("Capture reinitialization failed; retrying in 1 second.", initError!);
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        break;
                    }
                    continue;
                }

                _log.Info($"Capture reinitialized: {Width}x{Height}");
                interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, Fps));
                next = clock.Elapsed + interval;
            }

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
                catch (Exception initEx)
                {
                    // Any re-init failure (incl. InvalidOperationException when outputs momentarily
                    // vanish during a display-topology change) must NOT escape the loop and kill the
                    // capture task — log and retry on the next iteration instead.
                    _log.Error("캡처 재초기화 실패 — 1초 후 재시도", initEx);
                    Thread.Sleep(1000);
                }
            }

            // Hold the cadence; cancellable wait so stop is responsive.
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                if (ct.WaitHandle.WaitOne(wait))
                {
                    break;
                }
                next += interval;
            }
            else
            {
                next = clock.Elapsed + interval; // fell behind on a heavy frame; resync without bursting
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

        // Short poll: grab a new frame if one is ready, otherwise reuse the last (CFR fill).
        var result = duplication.TryAcquireNextFrame(5, out var frameInfo, out Resource? desktopResource);
        if (result.Failure)
        {
            if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
            {
                EmitFrame(); // no change → re-emit the last frame to keep a steady supply
                return;
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
            EmitFrame();
        }
        finally
        {
            desktopResource?.Dispose();
            duplication.ReleaseFrame();
        }
    }

    private void EmitFrame() =>
        FrameCaptured?.Invoke(this, new VideoFrame(Width, Height, DateTime.UtcNow, _frameBuffer!));

    private void CopyToBuffer(DataBox map)
    {
        var rowBytes = Width * 4;
        var fullFrame = _cropX == 0 && _cropY == 0 && Width == _monitorWidth && Height == _monitorHeight;
        if (fullFrame && map.RowPitch == rowBytes)
        {
            Utilities.Read(map.DataPointer, _frameBuffer, 0, _frameBuffer!.Length);
            return;
        }

        // Crop (or row-pitch padding): copy the region row by row from the staging surface.
        var srcColumnByteOffset = _cropX * 4;
        for (var y = 0; y < Height; y++)
        {
            var srcRow = map.DataPointer + ((_cropY + y) * map.RowPitch + srcColumnByteOffset);
            Utilities.Read(srcRow, _frameBuffer, y * rowBytes, rowBytes);
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
    /// alpha; masked/monochrome shapes are drawn opaque as an approximation). Cursor coordinates
    /// are monitor-relative, so the crop offset is subtracted to land in the emitted frame.
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

        // For Color cursors the 4th byte is straight alpha. For MaskedColor it is an AND/XOR mask
        // flag, NOT alpha, so blending it produces garbage — draw those opaque (the documented
        // approximation) instead.
        var isColorCursor = _cursorInfo.Type == (int)OutputDuplicatePointerShapeType.Color;
        var cursorWidth = _cursorInfo.Width;
        var cursorHeight = _cursorInfo.Height;
        for (var cy = 0; cy < cursorHeight; cy++)
        {
            var outY = _cursorPosition.Y + cy - _cropY;
            if (outY < 0 || outY >= Height)
            {
                continue;
            }
            for (var cx = 0; cx < cursorWidth; cx++)
            {
                var outX = _cursorPosition.X + cx - _cropX;
                if (outX < 0 || outX >= Width)
                {
                    continue;
                }

                var src = (cy * _cursorInfo.Pitch) + cx * 4;
                var dst = (outY * Width + outX) * 4;

                if (!isColorCursor)
                {
                    // MaskedColor: opaque copy of the colour bytes.
                    _frameBuffer[dst] = shape[src];
                    _frameBuffer[dst + 1] = shape[src + 1];
                    _frameBuffer[dst + 2] = shape[src + 2];
                    continue;
                }

                var alpha = shape[src + 3];
                if (alpha == 0)
                {
                    continue;
                }
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

    /// <summary>
    /// Enumerates desktop-attached outputs across all adapters in a stable order so the config
    /// monitor index, the UI picker and capture all agree. Returns adapter/output indices plus
    /// the display name, size and primary flag.
    /// </summary>
    private static List<(int AdapterIndex, int OutputIndex, string Name, int Width, int Height, bool Primary)>
        EnumerateAttachedOutputs(Factory1 factory)
    {
        var result = new List<(int, int, string, int, int, bool)>();
        var adapterCount = factory.GetAdapterCount1();
        for (var a = 0; a < adapterCount; a++)
        {
            Adapter1? adapter = null;
            try
            {
                adapter = factory.GetAdapter1(a);
                var outputCount = adapter.GetOutputCount();
                for (var o = 0; o < outputCount; o++)
                {
                    try
                    {
                        using var output = adapter.GetOutput(o);
                        var desc = output.Description;
                        if (!desc.IsAttachedToDesktop)
                        {
                            continue;
                        }
                        var b = desc.DesktopBounds;
                        var primary = b.Left == 0 && b.Top == 0;
                        result.Add((a, o, desc.DeviceName, b.Right - b.Left, b.Bottom - b.Top, primary));
                    }
                    catch (SharpDXException)
                    {
                        // skip an output that cannot be queried
                    }
                }
            }
            catch (SharpDXException)
            {
                // skip an adapter that cannot be enumerated (e.g. a virtual render-only device)
            }
            finally
            {
                adapter?.Dispose();
            }
        }
        return result;
    }

    /// <summary>Refresh rate of the given display via EnumDisplaySettings (plan §3.3: 주사율 따름).</summary>
    private static double GetRefreshRate(string? deviceName)
    {
        var devMode = new Devmode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Devmode>() };
        const int enumCurrentSettings = -1;
        if (EnumDisplaySettings(deviceName, enumCurrentSettings, ref devMode) && devMode.dmDisplayFrequency > 1)
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

/// <summary>Ensures a failed DXGI rebuild is clean and remains retryable.</summary>
internal static class CaptureRecoveryPolicy
{
    internal static bool TryInitialize(Action release, Action initialize, out Exception? error)
    {
        release();
        try
        {
            initialize();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            release();
            error = ex;
            return false;
        }
    }
}
