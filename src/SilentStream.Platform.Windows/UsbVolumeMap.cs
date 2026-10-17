using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SilentStream.Platform.Windows;

/// <summary>Storage bus type, rather than DriveType.Removable, includes USB SSD/UASP volumes.</summary>
public sealed class UsbVolumeMap
{
    private sealed record Volume(string Root, string Native, string Identity, string Connection);
    private Volume[] _volumes = [];
    public void Refresh()
    {
        var next = new List<Volume>();
        var previous = Volatile.Read(ref _volumes);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var root = drive.Name;
                if (root.Length != 3 || !drive.IsReady || !IsUsb(root)) continue;
                var native = new StringBuilder(1024);
                var identity = new StringBuilder(1024);
                if (QueryDosDevice(root[..2], native, native.Capacity) == 0
                    || !GetVolumeNameForVolumeMountPoint(root, identity, identity.Capacity)) continue;
                var old = previous.FirstOrDefault(v => v.Root == root && v.Identity == identity.ToString());
                next.Add(new(root, native.ToString(), identity.ToString(), old?.Connection ?? Guid.NewGuid().ToString("N")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Volatile.Write(ref _volumes, next.ToArray());
    }
    public (string Path, string Connection)? Resolve(string value)
    {
        foreach (var volume in Volatile.Read(ref _volumes))
        {
            if (value.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase)) return (value, volume.Connection);
            if (value.StartsWith(volume.Native + @"\", StringComparison.OrdinalIgnoreCase))
                return (volume.Root + value[(volume.Native.Length + 1)..], volume.Connection);
        }
        return null;
    }
    private static bool IsUsb(string root)
    {
        using var handle = CreateFile(@"\\.\" + root[..2], 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var result = new byte[4096];
        return DeviceIoControl(handle, 0x2D1400, new byte[12], 12, result, result.Length, out var bytes, IntPtr.Zero)
            && bytes >= 32 && BitConverter.ToUInt32(result, 28) == 7; // BusTypeUsb
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, int capacity);
}
