using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Capture.Cli;

/// <summary>
/// Tells a click of a person from a click made by a program. A low-level mouse hook sees every
/// mouse event before it reaches a window, and Windows marks events made with SendInput or
/// mouse_event as injected. Messages posted straight to a window (BM_CLICK, WM_LBUTTONUP) and UI
/// Automation never pass the hook at all. So a click counts only if the hook just saw a button
/// release that was not injected, at that spot on the screen.
///
/// This is what keeps the model from approving its own request: it runs commands as the same
/// user, and could otherwise press the button with any automation tool.
/// </summary>
internal sealed class PhysicalClickGuard : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x02;

    /// <summary>The hook's release and the window's mouse-up event must be at most this far apart.</summary>
    private const long MaxDelayMs = 500;

    private readonly HookProc _proc;
    private readonly IntPtr _hook;

    private long _upTicks = long.MinValue;
    private System.Drawing.Point _upPoint;
    private bool _downInjected = true, _upInjected = true;

    /// <summary>Installs the hook. It runs on the calling thread, which must pump messages (a WinForms UI thread).</summary>
    public PhysicalClickGuard()
    {
        _proc = OnMouse; // kept in a field so the delegate is not collected while the hook lives
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The mouse hook that checks for a real click could not be installed.");
        }
    }

    /// <summary>
    /// True if the left button was just pressed and released by a person (not injected), and the
    /// release happened inside <paramref name="screenBounds"/>.
    /// </summary>
    public bool WasPhysicalClick(System.Drawing.Rectangle screenBounds) =>
        !_downInjected && !_upInjected
        && Environment.TickCount64 - _upTicks <= MaxDelayMs
        && screenBounds.Contains(_upPoint);

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            if (message is WM_LBUTTONDOWN or WM_LBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                if (message == WM_LBUTTONDOWN)
                {
                    _downInjected = injected;
                }
                else
                {
                    _upInjected = injected;
                    _upTicks = Environment.TickCount64;
                    _upPoint = new System.Drawing.Point(info.pt.x, info.pt.y);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
