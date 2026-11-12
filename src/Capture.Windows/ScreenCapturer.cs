using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Capture.Core.Allowlist;
using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Pipeline;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace Capture.Windows;

/// <summary>
/// Captures the allowed windows, each through its own Windows.Graphics.Capture session, and builds
/// one composite per monitor. Pixels of non-allowed windows never enter this process: only allowed
/// windows are ever handed to the capture API.
///
/// Fails closed: any error, or a window layout that changed while capturing, discards everything.
/// </summary>
public sealed class ScreenCapturer
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly AllowlistMatcher _allowlist;
    private IDirect3DDevice? _device;

    public ScreenCapturer(AllowlistMatcher allowlist)
    {
        _allowlist = allowlist;
    }

    /// <summary>False if Windows refused borderless capture; captured windows then show a yellow border on screen.</summary>
    public bool IsBorderless { get; private set; }

    /// <summary>Requests borderless capture and creates the graphics device. Call once at startup.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            AppCapabilityAccessStatus access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            IsBorderless = access == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception)
        {
            IsBorderless = false;
        }

        _device = CreateDirect3DDevice();
    }

    /// <summary>
    /// Takes one capture. On a layout mismatch it retries once after 500 ms, then gives up.
    /// The caller owns the returned composites and must dispose them.
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        string reason = "unknown";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }

            try
            {
                CaptureResult? result = await TryCaptureAsync(timestamp, cancellationToken);
                if (result is not null)
                {
                    return result;
                }
                reason = "window layout changed during capture";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Type and code only: exception messages are not guaranteed to be free of content.
                reason = $"{ex.GetType().Name} 0x{ex.HResult:X8}";
            }
        }

        return CaptureResult.Discarded(reason);
    }

    /// <summary>Returns null if the window layout changed between the start and the end of the capture.</summary>
    private async Task<CaptureResult?> TryCaptureAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        CapturePlan before = ReadPlan();
        if (!before.HasAllowedWindows)
        {
            return CaptureResult.NoAllowedWindow;
        }

        WindowFrame[] frames = [];
        var composites = new List<RawComposite>();
        bool success = false;
        try
        {
            frames = await Task.WhenAll(before.WindowsToCapture.Select(w => CaptureWindowAsync(w, cancellationToken)));
            var frameByHandle = frames.ToDictionary(f => f.Window.Handle);

            foreach (MonitorPlan monitor in before.Monitors)
            {
                RectInt bounds = monitor.Monitor.Bounds;
                byte[] canvas = new byte[checked(bounds.Width * bounds.Height * Compositor.BytesPerPixel)];
                var composite = new RawComposite(timestamp, monitor.Monitor, bounds.Width, bounds.Height, canvas, monitor.Windows);
                composites.Add(composite);

                foreach (WindowPlacement placement in monitor.Windows)
                {
                    WindowFrame frame = frameByHandle[placement.Window.Handle];
                    Compositor.Blit(canvas, bounds, frame.Pixels, frame.Width, frame.Height, frame.Origin, placement.VisibleRects);
                }
            }

            // Anything that moved, resized, changed z-order, appeared or disappeared meanwhile
            // makes the composite untrustworthy.
            CapturePlan after = ReadPlan();
            if (!before.HasSameLayoutAs(after))
            {
                return null;
            }

            success = true;
            return new CaptureResult(CaptureOutcome.Captured, composites);
        }
        finally
        {
            foreach (WindowFrame frame in frames)
            {
                Array.Clear(frame.Pixels);
            }
            if (!success)
            {
                foreach (RawComposite composite in composites)
                {
                    composite.Dispose();
                }
            }
        }
    }

    private CapturePlan ReadPlan() =>
        CapturePlanner.Plan(DesktopInspector.EnumerateWindows(_allowlist), DesktopInspector.EnumerateMonitors());

    private sealed record WindowFrame(WindowInfo Window, int Width, int Height, (int X, int Y) Origin, byte[] Pixels);

    /// <summary>
    /// Captures one allowed window. A session is opened per capture and closed right after the
    /// first frame: the first frame always shows the current content, and nothing runs in between.
    /// </summary>
    private async Task<WindowFrame> CaptureWindowAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (!window.IsAllowed)
        {
            throw new InvalidOperationException("Refusing to capture a window that is not allowed.");
        }
        IDirect3DDevice device = _device ?? throw new InvalidOperationException("InitializeAsync has not been called.");

        GraphicsCaptureItem item = CreateItemForWindow((nint)window.Handle);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using GraphicsCaptureSession session = pool.CreateCaptureSession(item);

        session.IsCursorCaptureEnabled = false;
        if (IsBorderless)
        {
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception)
            {
                IsBorderless = false;
            }
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

        Direct3D11CaptureFrame? arrived = await firstFrame.Task.WaitAsync(FrameTimeout, cancellationToken);
        using Direct3D11CaptureFrame frame = arrived ?? throw new InvalidOperationException("Capture returned no frame.");

        using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if (frame.ContentSize.Width != width || frame.ContentSize.Height != height)
        {
            throw new InvalidOperationException("Window was resized during capture.");
        }

        // The frame normally matches the visible frame bounds exactly. Some windows are captured
        // including their invisible resize borders; then it matches GetWindowRect instead.
        (int X, int Y) origin;
        if (width == window.Bounds.Width && height == window.Bounds.Height)
        {
            origin = (window.Bounds.Left, window.Bounds.Top);
        }
        else if (width == window.WindowRect.Width && height == window.WindowRect.Height)
        {
            origin = (window.WindowRect.Left, window.WindowRect.Top);
        }
        else
        {
            throw new InvalidOperationException("Captured frame size does not match the window bounds.");
        }

        byte[] pixels = new byte[checked(width * height * Compositor.BytesPerPixel)];
        try
        {
            bitmap.CopyToBuffer(pixels.AsBuffer());
        }
        catch
        {
            Array.Clear(pixels);
            throw;
        }
        return new WindowFrame(window, width, height, origin, pixels);
    }

    // ---- Interop ----

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    private static IDirect3DDevice CreateDirect3DDevice()
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

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
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
