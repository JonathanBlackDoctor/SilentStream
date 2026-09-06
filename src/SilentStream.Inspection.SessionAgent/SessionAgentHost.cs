using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SilentStream.Inspection;

namespace SilentStream.Inspection.SessionAgent;

public sealed class SessionAgentHost
{
    private readonly DxgiDesktopCapture _capture = new();
    private readonly object _inputGate = new();
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private volatile bool _controlDisabled;
    private bool _emergencyLatched;

    public async Task RunAsync(CancellationToken token)
    {
        _ = MonitorEmergencyChordAsync(token);
        while (!token.IsCancellationRequested)
        {
            var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, token);
            }
            catch { pipe.Dispose(); if (token.IsCancellationRequested) return; }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(CoordinatorServiceSid(), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(InspectionProtocol.SessionPipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            16 * 1024, 16 * 1024, security);
    }

    private static SecurityIdentifier CoordinatorServiceSid()
    {
        try { return (SecurityIdentifier)new NTAccount("NT SERVICE", "SilentStreamInspectionCoordinator").Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException ex) { throw new InvalidOperationException("Coordinator service SID is unavailable; refusing to create a broader session pipe ACL.", ex); }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                var request = await ReadAsync<SessionRequest>(pipe, token).ConfigureAwait(false);
                var response = await ExecuteAsync(request, token).ConfigureAwait(false);
                await WriteAsync(pipe, response, token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task<SessionResponse> ExecuteAsync(SessionRequest request, CancellationToken token)
    {
        try
        {
            object? payload = request.Action switch
            {
                "ping" => new { version = InspectionProtocol.AgentVersion, user = Environment.UserName, sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId },
                "monitors" => new { monitors = _capture.GetMonitors() },
                "capture" => Capture(request.Payload),
                "self-test" => SelfTest(),
                "audio-self-test" => await AudioPhysicalProbe.RunAsync(
                    AudioPhysicalProbe.ParseOptions(request.Payload), token).ConfigureAwait(false),
                "input" => Inject(request.Payload),
                "release-input" => ReleaseInput(),
                "reset-control" => ResetControl(),
                "start-app" => await StartAppAsync(request.Payload).ConfigureAwait(false),
                "install-app" => await InstallAppAsync(request.Payload).ConfigureAwait(false),
                "uninstall-app" => await UninstallAppAsync(request.Payload).ConfigureAwait(false),
                _ => throw new InvalidDataException("Unknown session action.")
            };
            return new SessionResponse(request.Id, true, null, JsonSerializer.SerializeToElement(payload, InspectionProtocol.Json));
        }
        catch (Exception ex) { return new SessionResponse(request.Id, false, ex.Message); }
    }

    private object Capture(JsonElement? payload)
    {
        var input = payload ?? throw new InvalidDataException("Capture payload is missing.");
        var monitorIndex = input.GetProperty("monitor").GetInt32();
        var maxWidth = Math.Clamp(input.GetProperty("maxWidth").GetInt32(), 320, 1920);
        var quality = Math.Clamp(input.GetProperty("quality").GetInt32(), 30, 90);
        var frame = _capture.Capture(monitorIndex, maxWidth, quality);
        return new { jpegBase64 = Convert.ToBase64String(frame.Jpeg), frame.Width, frame.Height, captureMethod = "dxgi",
            controlDisabled = _controlDisabled, capturedAtUtc = DateTimeOffset.UtcNow };
    }

    private object Inject(JsonElement? payload)
    {
        if (_controlDisabled) throw new InvalidOperationException("Remote input was revoked locally. Acquire a new control lease to resume.");
        var input = payload?.Deserialize<DesktopInput>(InspectionProtocol.Json) ?? throw new InvalidDataException("Input payload is missing.");
        var bounds = _capture.GetBounds(input.Monitor);
        lock (_inputGate)
        {
            switch (input.Kind)
            {
                case "move": MoveMouse(bounds, input.X, input.Y); break;
                case "button": MouseButton(input.Button, input.Down); break;
                case "wheel": SendMouse(0, 0, (uint)MouseFlags.Wheel, input.Delta); break;
                case "key": Keyboard(input.Key, input.Down); break;
                default: throw new InvalidDataException("Input kind is not allowed.");
            }
        }
        return new { accepted = true };
    }

    private object SelfTest()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100);
        var inputDesktopAvailable = desktop != nint.Zero;
        if (desktop != nint.Zero) CloseDesktop(desktop);
        return new
        {
            dxgiMonitors = _capture.GetMonitors(),
            virtualScreen = new { SystemInformation.VirtualScreen.X, SystemInformation.VirtualScreen.Y, SystemInformation.VirtualScreen.Width, SystemInformation.VirtualScreen.Height },
            systemDpi = GetDpiForSystem(), inputDesktopAvailable,
            processSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
            emergencyChord = "Ctrl+Alt+Shift+F12",
            consoleWindowVisible = GetConsoleWindow() != nint.Zero
        };
    }

    private static async Task<object> StartAppAsync(JsonElement? payload)
    {
        var app = RequiredPath(payload, "appExecutablePath", "MediaCaptureHelper.exe");
        _ = Process.Start(Hidden(app)) ?? throw new InvalidOperationException("Application did not start in the interactive session.");
        await Task.Delay(750).ConfigureAwait(false);
        return new { started = true, sessionId = Process.GetCurrentProcess().SessionId };
    }

    private static async Task<object> InstallAppAsync(JsonElement? payload)
    {
        var installer = RequiredPath(payload, "installerPath", null);
        if (!Path.GetFileName(installer).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer must be an executable file.");
        var expected = RequiredString(payload, "sha256");
        var installRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
            ?? throw new InvalidDataException("Inspection install root is unavailable.");
        var productRoot = Directory.GetParent(installRoot)?.FullName
            ?? throw new InvalidDataException("Inspection product root is unavailable.");
        var approvedRoot = Path.GetFullPath(Path.Combine(productRoot, "InspectionPolicy", "installer-cache")) + Path.DirectorySeparatorChar;
        if (!installer.StartsWith(approvedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Installer is outside the inspection installer cache.");
        await using var installerStream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(installerStream).ConfigureAwait(false)).ToLowerInvariant();
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new CryptographicException("Installer hash changed after broker approval.");
        await RunHiddenAsync(installer, TimeSpan.FromMinutes(5), "--silent").ConfigureAwait(false);
        return new { installed = true, sha256 = actual };
    }

    private static async Task<object> UninstallAppAsync(JsonElement? payload)
    {
        var app = RequiredPath(payload, "appExecutablePath", "MediaCaptureHelper.exe");
        var updater = RequiredPath(payload, "updaterPath", "Update.exe");
        var expectedUpdater = Path.GetFullPath(Path.Combine(Directory.GetParent(Directory.GetParent(app)?.FullName ?? string.Empty)?.FullName ?? string.Empty, "Update.exe"));
        if (!updater.Equals(expectedUpdater, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Updater is not associated with the configured application.");
        await RunHiddenAsync(updater, TimeSpan.FromMinutes(3), "uninstall", "--silent").ConfigureAwait(false);
        return new { uninstalled = true };
    }

    private static ProcessStartInfo Hidden(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task RunHiddenAsync(string path, TimeSpan timeout, params string[] arguments)
    {
        using var process = Process.Start(Hidden(path, arguments)) ?? throw new InvalidOperationException("Approved process did not start.");
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException("Approved process exceeded its time limit."); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Approved process exited with {process.ExitCode}.");
    }

    private static string RequiredPath(JsonElement? payload, string property, string? exactFileName)
    {
        var value = RequiredString(payload, property);
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new FileNotFoundException($"Approved {property} is unavailable.", value);
        var path = Path.GetFullPath(value);
        if (exactFileName is not null && !Path.GetFileName(path).Equals(exactFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{property} has an unexpected file name.");
        return path;
    }

    private static string RequiredString(JsonElement? payload, string property) =>
        payload is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var item) && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()! : throw new InvalidDataException($"Missing payload.{property}.");

    private object ResetControl() { ReleaseInput(); _controlDisabled = false; return new { reset = true }; }

    private object ReleaseInput()
    {
        lock (_inputGate)
        {
            foreach (var key in _pressedKeys.ToArray()) SendKey(key, false);
            foreach (var button in _pressedButtons.ToArray()) SendButton(button, false);
            _pressedKeys.Clear(); _pressedButtons.Clear();
        }
        return new { released = true };
    }

    private async Task MonitorEmergencyChordAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var down = IsDown(0x11) && IsDown(0x12) && IsDown(0x10) && IsDown(0x7B); // Ctrl+Alt+Shift+F12
            if (down && !_emergencyLatched) { _emergencyLatched = true; _controlDisabled = true; ReleaseInput(); }
            if (!down) _emergencyLatched = false;
            await Task.Delay(75, token).ConfigureAwait(false);
        }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void MoveMouse(Rectangle bounds, double x, double y)
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        var px = bounds.Left + Math.Clamp(x, 0, 1) * Math.Max(1, bounds.Width - 1);
        var py = bounds.Top + Math.Clamp(y, 0, 1) * Math.Max(1, bounds.Height - 1);
        var nx = (int)Math.Round((px - virtualBounds.Left) * 65535 / Math.Max(1, virtualBounds.Width - 1));
        var ny = (int)Math.Round((py - virtualBounds.Top) * 65535 / Math.Max(1, virtualBounds.Height - 1));
        SendMouse(nx, ny, (uint)(MouseFlags.Move | MouseFlags.Absolute | MouseFlags.VirtualDesk), 0);
    }

    private void MouseButton(int button, bool down)
    {
        SendButton(button, down);
        if (down) _pressedButtons.Add(button); else _pressedButtons.Remove(button);
    }

    private static void SendButton(int button, bool down)
    {
        var flag = button switch
        {
            0 => down ? MouseFlags.LeftDown : MouseFlags.LeftUp,
            1 => down ? MouseFlags.MiddleDown : MouseFlags.MiddleUp,
            2 => down ? MouseFlags.RightDown : MouseFlags.RightUp,
            _ => throw new InvalidDataException("Mouse button is not allowed.")
        };
        SendMouse(0, 0, (uint)flag, 0);
    }

    private void Keyboard(int key, bool down)
    {
        if (key is < 1 or > 255) throw new InvalidDataException("Virtual key is outside the allowed range.");
        SendKey(key, down);
        if (down) _pressedKeys.Add(key); else _pressedKeys.Remove(key);
    }

    private static void SendKey(int key, bool down)
    {
        var input = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = down ? 0u : 0x0002u } } };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new System.ComponentModel.Win32Exception();
    }

    private static void SendMouse(int x, int y, uint flags, int data)
    {
        var input = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, mouseData = unchecked((uint)data), dwFlags = flags } } };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new System.ComponentModel.Win32Exception();
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BitConverter.ToInt32(header);
        if (length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid pipe message.");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, InspectionProtocol.Json) ?? throw new InvalidDataException("Empty pipe message.");
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, InspectionProtocol.Json); await stream.WriteAsync(BitConverter.GetBytes(data.Length), token);
        await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }

    [Flags] private enum MouseFlags : uint { Move=0x0001,LeftDown=0x0002,LeftUp=0x0004,RightDown=0x0008,RightUp=0x0010,MiddleDown=0x0020,MiddleUp=0x0040,Wheel=0x0800,VirtualDesk=0x4000,Absolute=0x8000 }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public nuint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk,wScan; public uint dwFlags,time; public nuint dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
}
