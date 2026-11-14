using Capture.Core.Rules;
using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Capture.Windows;

/// <summary>Reads the current window and monitor layout. Stateless; safe to call from any thread.</summary>
public static class DesktopInspector
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int MaxOwnerDepth = 8;

    /// <summary>
    /// Visible top-level windows, topmost first. Minimized, cloaked (other virtual desktops, hidden
    /// UWP windows) and zero-size windows are skipped. Process name and title are only kept for
    /// capturable windows.
    /// </summary>
    public static unsafe List<WindowInfo> EnumerateWindows(WindowRuleMatcher rules)
    {
        var handles = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, default);

        var result = new List<WindowInfo>();
        // Whether each handle is capturable, so owned windows (menus, dialogs) can inherit it from their owner.
        var capturableByHandle = new Dictionary<nint, (bool Capturable, string? Process, string? Title)>();

        foreach (HWND hwnd in handles)
        {
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
            {
                continue;
            }

            int cloaked = 0;
            if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded && cloaked != 0)
            {
                continue;
            }

            RECT frame;
            if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed)
            {
                continue;
            }
            RectInt bounds = ToRect(frame);
            if (bounds.IsEmpty)
            {
                continue;
            }

            if (!PInvoke.GetWindowRect(hwnd, out RECT windowRect))
            {
                continue;
            }

            var (capturable, process, title) = Classify(hwnd, rules, capturableByHandle, depth: 0);

            // Click-through layered windows are overlays (GPU tools, screen annotation, ...) that
            // hide nothing. Treating them as transparent only affects which parts of capturable
            // windows count as visible; a other window is never captured either way.
            int exStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            bool clickThroughOverlay = (exStyle & WS_EX_TRANSPARENT) != 0 && (exStyle & WS_EX_LAYERED) != 0;

            result.Add(new WindowInfo(
                (long)(nint)hwnd, bounds, ToRect(windowRect), result.Count,
                capturable, IsCovering: capturable || !clickThroughOverlay,
                capturable ? process : null, capturable ? title : null));
        }

        return result;
    }

    /// <summary>All monitors: the primary is index 1, the others follow left to right, top to bottom.</summary>
    public static unsafe List<MonitorInfo> EnumerateMonitors()
    {
        var found = new List<(HMONITOR Handle, string Device, RectInt Bounds, double Scale, bool Primary)>();

        PInvoke.EnumDisplayMonitors(default, (RECT*)null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                double scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded
                    ? dpiX / 96.0
                    : 1.0;
                const uint MONITORINFOF_PRIMARY = 1;
                found.Add((monitor, info.szDevice.ToString(), ToRect(info.monitorInfo.rcMonitor), scale,
                    (info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, default);

        return found
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .Select((m, i) => new MonitorInfo(i + 1, (long)(nint)m.Handle, m.Device, m.Bounds, m.Scale, m.Primary))
            .ToList();
    }

    /// <summary>
    /// A window is capturable if a capture rule matches its own process and title, or if it is owned by an
    /// capturable window (menus, dropdowns, dialogs).
    /// </summary>
    private static (bool Capturable, string? Process, string? Title) Classify(
        HWND hwnd, WindowRuleMatcher rules, Dictionary<nint, (bool, string?, string?)> cache, int depth)
    {
        if (cache.TryGetValue(hwnd, out var known))
        {
            return known;
        }

        (bool Capturable, string? Process, string? Title) result = (false, null, null);

        // Never captured, whatever the rules say: this app's own windows (its tray menu) and the
        // Windows shell (desktop, taskbar). They still count as covering what is behind them.
        uint processId = GetProcessId(hwnd);
        if (processId == Environment.ProcessId || IsShellWindow(hwnd))
        {
            cache[hwnd] = result;
            return result;
        }

        string? process = GetProcessName(processId);
        if (process is not null && rules.HasRuleFor(process))
        {
            string title = GetTitle(hwnd);
            if (rules.ShouldCapture(process, title))
            {
                result = (true, process, title);
            }
        }

        if (!result.Capturable && depth < MaxOwnerDepth)
        {
            HWND owner = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER);
            if (owner != default && owner != hwnd && Classify(owner, rules, cache, depth + 1).Capturable)
            {
                string title = GetTitle(hwnd);
                // The owner rule never overrides the private-window rule.
                if (process is not null && !WindowRuleMatcher.IsPrivateWindowTitle(title))
                {
                    result = (true, process, title);
                }
            }
        }

        cache[hwnd] = result;
        return result;
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private static unsafe bool IsShellWindow(HWND hwnd)
    {
        char* buffer = stackalloc char[128];
        int length = PInvoke.GetClassName(hwnd, buffer, 128);
        return length > 0 && ShellWindowClasses.Contains(new string(buffer, 0, length));
    }

    internal static unsafe uint GetProcessId(HWND hwnd)
    {
        if (hwnd == default)
        {
            return 0;
        }
        uint processId = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        return processId;
    }

    /// <summary>Executable file name such as "Code.exe", or null if the process cannot be queried.</summary>
    internal static unsafe string? GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == default)
        {
            return null; // gone or not accessible: treated as not capturable
        }

        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size) || size == 0)
            {
                return null;
            }
            return Path.GetFileName(new string(buffer, 0, (int)size));
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    internal static unsafe string GetTitle(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }
        char[] buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            int copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return copied > 0 ? new string(p, 0, copied) : "";
        }
    }

    private static RectInt ToRect(RECT r) => new(r.left, r.top, r.right, r.bottom);
}
