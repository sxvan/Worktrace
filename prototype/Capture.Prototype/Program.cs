// M0 prototype: capture individual windows via Windows.Graphics.Capture and save each as PNG.
// Debug only. This project writes raw captures to disk and must never be part of the shipped app.
//
// Usage: Capture.Prototype [process names...]     (default: Code claude WindowsTerminal)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

string[] targets = args.Length > 0 ? args : ["Code", "claude", "WindowsTerminal"];
targets = targets.Select(t => t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t).ToArray();

string outputDir = Path.Combine(Environment.CurrentDirectory, "m0-output");
Directory.CreateDirectory(outputDir);

Console.WriteLine($"Capture supported: {GraphicsCaptureSession.IsSupported()}");

// Borderless capture has to be requested once per process.
bool borderless = false;
try
{
    var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
    borderless = access == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
    Console.WriteLine($"Borderless access: {access}");
}
catch (Exception ex)
{
    Console.WriteLine($"Borderless access request failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
}

var windows = Native.EnumerateCandidateWindows();
IDirect3DDevice device = Native.CreateDirect3DDevice();

foreach (string target in targets)
{
    Console.WriteLine();
    var matches = windows.Where(w => string.Equals(w.ProcessName, target, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
    {
        Console.WriteLine($"[{target}] no visible, non-minimized window found");
        Console.WriteLine($"  Processes with capturable windows: {string.Join(", ", windows.Select(w => w.ProcessName).Distinct().Order())}");
        continue;
    }

    // First match is the topmost one in z-order.
    var window = matches[0];
    Console.WriteLine($"[{target}] hwnd=0x{window.Handle:X} ({matches.Count} matching window(s), using topmost)");
    Console.WriteLine($"  DWM frame bounds : {window.FrameBounds}");
    Console.WriteLine($"  GetWindowRect    : {window.WindowRect}");
    Console.WriteLine($"  Window DPI       : {window.Dpi} ({window.Dpi * 100 / 96}%)");

    try
    {
        await CaptureWindowAsync(window, target);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAILED: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"Output folder: {outputDir}");

async Task CaptureWindowAsync(WindowInfo window, string name)
{
    var stopwatch = Stopwatch.StartNew();
    GraphicsCaptureItem item = Native.CreateItemForWindow(window.Handle);
    Console.WriteLine($"  Capture item size: {item.Size.Width}x{item.Size.Height}");

    using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
        device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
    using var session = pool.CreateCaptureSession(item);

    session.IsCursorCaptureEnabled = false;
    try
    {
        session.IsBorderRequired = !borderless;
        Console.WriteLine($"  Border required  : {session.IsBorderRequired}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Could not set IsBorderRequired: {ex.GetType().Name} 0x{ex.HResult:X8}");
    }

    var firstFrame = new TaskCompletionSource<Direct3D11CaptureFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
    pool.FrameArrived += (sender, _) =>
    {
        if (!firstFrame.Task.IsCompleted)
        {
            firstFrame.TrySetResult(sender.TryGetNextFrame());
        }
    };

    session.StartCapture();

    var completed = await Task.WhenAny(firstFrame.Task, Task.Delay(3000));
    if (completed != firstFrame.Task || firstFrame.Task.Result is null)
    {
        Console.WriteLine("  No frame within 3 s");
        return;
    }

    using Direct3D11CaptureFrame frame = firstFrame.Task.Result;
    Console.WriteLine($"  Frame content    : {frame.ContentSize.Width}x{frame.ContentSize.Height} after {stopwatch.ElapsedMilliseconds} ms");

    using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);

    var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
    bitmap.CopyToBuffer(buffer);
    byte[] pixels = buffer.ToArray();
    bool allBlack = true;
    for (int i = 0; i < pixels.Length; i += 4)
    {
        if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
        {
            allBlack = false;
            break;
        }
    }
    Array.Clear(pixels);
    Console.WriteLine($"  All black        : {allBlack}");

    string path = Path.Combine(outputDir, $"{name}.png");
    using (var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite))
    using (var stream = file.AsRandomAccessStream())
    {
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
    Console.WriteLine($"  Saved            : {path} ({stopwatch.ElapsedMilliseconds} ms total)");
}

record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

record WindowInfo(nint Handle, string ProcessName, Rect FrameBounds, Rect WindowRect, uint Dpi);

static class Native
{
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    /// <summary>Visible, non-minimized, non-cloaked, titled top-level windows in z-order (topmost first).</summary>
    public static List<WindowInfo> EnumerateCandidateWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindowTextLengthW(hwnd) == 0)
            {
                return true;
            }
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out Rect frame, Marshal.SizeOf<Rect>()) != 0
                || frame.Width <= 0 || frame.Height <= 0)
            {
                return true;
            }
            GetWindowRect(hwnd, out Rect windowRect);
            GetWindowThreadProcessId(hwnd, out uint pid);
            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                return true;
            }
            result.Add(new WindowInfo(hwnd, processName, frame, windowRect, GetDpiForWindow(hwnd)));
            return true;
        }, 0);
        return result;
    }

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;

        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            0, 0, D3D11_SDK_VERSION, out nint d3dDevice, out _, out nint context));
        try
        {
            Guid dxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in dxgiDeviceIid, out nint dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
    }

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        nint itemPointer = interop.CreateForWindow(hwnd, ref itemIid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }
}
