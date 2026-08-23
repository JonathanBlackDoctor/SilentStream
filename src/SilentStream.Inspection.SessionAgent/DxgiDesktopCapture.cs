using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SilentStream.Inspection;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace SilentStream.Inspection.SessionAgent;

/// <summary>Independent on-demand Desktop Duplication capture used only by the inspection session agent.</summary>
public sealed class DxgiDesktopCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CaptureSession> _sessions = [];

    public DesktopMonitor[] GetMonitors()
    {
        using var factory = new Factory1();
        return Enumerate(factory).Select((o, index) => new DesktopMonitor(index, o.Bounds.Left, o.Bounds.Top,
            o.Bounds.Right - o.Bounds.Left, o.Bounds.Bottom - o.Bounds.Top, o.Bounds.Left == 0 && o.Bounds.Top == 0, o.Name)).ToArray();
    }

    public Rectangle GetBounds(int monitor)
    {
        var item = GetMonitors().ElementAtOrDefault(monitor) ?? throw new InvalidDataException("Monitor does not exist.");
        return new Rectangle(item.X, item.Y, item.Width, item.Height);
    }

    public CapturedDesktopFrame Capture(int monitor, int maxWidth, int quality)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(monitor, out var session)) _sessions[monitor] = session = CreateSession(monitor);
            try { return session.Capture(maxWidth, quality); }
            catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.AccessLost || ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
            {
                session.Dispose(); _sessions[monitor] = session = CreateSession(monitor);
                return session.Capture(maxWidth, quality);
            }
        }
    }

    private static CaptureSession CreateSession(int monitor)
    {
        using var factory = new Factory1();
        var outputs = Enumerate(factory);
        if (monitor < 0 || monitor >= outputs.Count) throw new InvalidDataException("Monitor does not exist.");
        var selected = outputs[monitor];
        var adapter = factory.GetAdapter1(selected.AdapterIndex);
        try
        {
            var device = new Device(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
            try
            {
                using var output = adapter.GetOutput(selected.OutputIndex);
                using var output1 = output.QueryInterface<Output1>();
                return new CaptureSession(device, output1.DuplicateOutput(device), selected.Bounds.Left, selected.Bounds.Top,
                    selected.Bounds.Right - selected.Bounds.Left, selected.Bounds.Bottom - selected.Bounds.Top);
            }
            catch { device.Dispose(); throw; }
        }
        finally { adapter.Dispose(); }
    }

    private static List<OutputItem> Enumerate(Factory1 factory)
    {
        var values = new List<OutputItem>();
        for (var a = 0; a < factory.GetAdapterCount1(); a++)
        {
            using var adapter = factory.GetAdapter1(a);
            for (var o = 0; o < adapter.GetOutputCount(); o++)
            {
                using var output = adapter.GetOutput(o); var description = output.Description;
                if (description.IsAttachedToDesktop) values.Add(new OutputItem(a, o, description.DeviceName, description.DesktopBounds));
            }
        }
        return values;
    }

    public void Dispose() { lock (_gate) { foreach (var session in _sessions.Values) session.Dispose(); _sessions.Clear(); } }
    private sealed record OutputItem(int AdapterIndex, int OutputIndex, string Name, SharpDX.Mathematics.Interop.RawRectangle Bounds);

    private sealed class CaptureSession : IDisposable
    {
        private readonly Device _device; private readonly OutputDuplication _duplication; private readonly Texture2D _staging;
        private readonly int _left, _top, _width, _height; private byte[]? _lastBgra;
        public CaptureSession(Device device, OutputDuplication duplication, int left, int top, int width, int height)
        {
            _device=device;_duplication=duplication;_left=left;_top=top;_width=width;_height=height;
            _staging=new Texture2D(device,new Texture2DDescription{Width=width,Height=height,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Staging,CpuAccessFlags=CpuAccessFlags.Read,BindFlags=BindFlags.None,OptionFlags=ResourceOptionFlags.None});
        }
        public CapturedDesktopFrame Capture(int maxWidth,int quality)
        {
            var result=_duplication.TryAcquireNextFrame(_lastBgra is null?500:5,out _,out Resource? resource);
            if(result.Success)
            {
                try
                {
                    using(var texture=resource!.QueryInterface<Texture2D>())_device.ImmediateContext.CopyResource(texture,_staging);
                    var map=_device.ImmediateContext.MapSubresource(_staging,0,MapMode.Read,MapFlags.None);
                    try
                    {
                        _lastBgra=new byte[_width*_height*4];
                        for(var y=0;y<_height;y++) Utilities.Read(map.DataPointer+y*map.RowPitch,_lastBgra,y*_width*4,_width*4);
                    }
                    finally{_device.ImmediateContext.UnmapSubresource(_staging,0);}
                }
                finally{resource?.Dispose();_duplication.ReleaseFrame();}
            }
            else if(result.Code!=SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)result.CheckError();
            if(_lastBgra is null)throw new TimeoutException("DXGI did not provide an initial desktop frame.");
            using var source=new Bitmap(_width,_height,PixelFormat.Format32bppArgb);
            var bits=source.LockBits(new Rectangle(0,0,_width,_height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try{for(var y=0;y<_height;y++)Marshal.Copy(_lastBgra,y*_width*4,bits.Scan0+y*bits.Stride,_width*4);}finally{source.UnlockBits(bits);}
            DrawCursor(source,_left,_top);
            var width=Math.Min(_width,maxWidth);var height=Math.Max(1,(int)Math.Round(_height*(width/(double)_width)));
            using var scaled=new Bitmap(width,height,PixelFormat.Format24bppRgb);
            using(var graphics=Graphics.FromImage(scaled)){graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.Bilinear;graphics.DrawImage(source,0,0,width,height);}
            using var stream=new MemoryStream();var encoder=ImageCodecInfo.GetImageEncoders().First(e=>e.FormatID==ImageFormat.Jpeg.Guid);
            using var parameters=new EncoderParameters(1);parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,Math.Clamp(quality,30,90));scaled.Save(stream,encoder,parameters);
            return new CapturedDesktopFrame(stream.ToArray(),width,height);
        }
        public void Dispose(){_duplication.Dispose();_staging.Dispose();_device.Dispose();}
    }

    private static void DrawCursor(Bitmap target, int monitorLeft, int monitorTop)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || info.flags != 1 || info.hCursor == nint.Zero || !GetIconInfo(info.hCursor, out var iconInfo)) return;
        try
        {
            var x = info.ptScreenPos.X - monitorLeft - (int)iconInfo.xHotspot;
            var y = info.ptScreenPos.Y - monitorTop - (int)iconInfo.yHotspot;
            if (x >= target.Width || y >= target.Height || x < -128 || y < -128) return;
            using var icon = (Icon)Icon.FromHandle(info.hCursor).Clone();
            using var graphics = Graphics.FromImage(target);
            graphics.DrawIconUnstretched(icon, new Rectangle(x, y, icon.Width, icon.Height));
        }
        finally
        {
            if (iconInfo.hbmMask != nint.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != nint.Zero) DeleteObject(iconInfo.hbmColor);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public nint hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public uint xHotspot, yHotspot; public nint hbmMask, hbmColor; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}

public sealed record CapturedDesktopFrame(byte[] Jpeg,int Width,int Height);
