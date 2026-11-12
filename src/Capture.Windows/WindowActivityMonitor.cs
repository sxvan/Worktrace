using System.Windows.Forms;
using Capture.Core.Allowlist;
using Capture.Core.Monitoring;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Capture.Windows;

/// <summary>
/// Watches foreground window changes, title changes of the foreground window, input activity,
/// session lock/unlock and sleep/resume. Runs on its own thread with a message loop and reports
/// everything through a single callback, in order. Installs no keyboard or mouse hooks.
/// </summary>
public sealed class WindowActivityMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const int WM_CLOSE = 0x0010;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private readonly AllowlistMatcher _allowlist;
    private readonly TimeSpan _pollInterval;
    private readonly Action<MonitorSignal> _onSignal;
    private readonly Action<Exception> _onError;
    private readonly TimeProvider _time;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly WINEVENTPROC _winEventProc; // field keeps the delegate alive while hooks are installed

    private MessageWindow? _window;
    private nint _windowHandle;
    private HWINEVENTHOOK _foregroundHook;
    private HWINEVENTHOOK _nameChangeHook;
    private HWND _foreground;
    private bool _foregroundHasRule;
    private WindowSnapshot? _lastSnapshot;

    public WindowActivityMonitor(
        AllowlistMatcher allowlist,
        TimeSpan pollInterval,
        Action<MonitorSignal> onSignal,
        Action<Exception> onError,
        TimeProvider? timeProvider = null)
    {
        _allowlist = allowlist;
        _pollInterval = pollInterval;
        _onSignal = onSignal;
        _onError = onError;
        _time = timeProvider ?? TimeProvider.System;
        _winEventProc = OnWinEvent;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "WindowActivityMonitor" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
    }

    public void Dispose()
    {
        if (_thread.IsAlive && _windowHandle != 0)
        {
            PInvoke.PostMessage((HWND)_windowHandle, WM_CLOSE, default, default);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        _started.Dispose();
    }

    private void ThreadMain()
    {
        System.Windows.Forms.Timer? timer = null;
        try
        {
            _window = new MessageWindow(this);
            _windowHandle = _window.Handle;

            if (!PInvoke.WTSRegisterSessionNotification((HWND)_windowHandle, NOTIFY_FOR_THIS_SESSION))
            {
                _onError(new InvalidOperationException("WTSRegisterSessionNotification failed; lock/unlock will not be detected."));
            }

            _foregroundHook = PInvoke.SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, HMODULE.Null, _winEventProc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (_foregroundHook == default)
            {
                throw new InvalidOperationException("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed.");
            }

            timer = new System.Windows.Forms.Timer { Interval = Math.Max(100, (int)_pollInterval.TotalMilliseconds) };
            timer.Tick += (_, _) => Guard(PollInput);
            timer.Start();

            Guard(() => HandleForeground(PInvoke.GetForegroundWindow()));
            _started.Set();

            Application.Run();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            _started.Set();
            timer?.Dispose();
            Unhook(ref _nameChangeHook);
            Unhook(ref _foregroundHook);
            if (_windowHandle != 0)
            {
                PInvoke.WTSUnRegisterSessionNotification((HWND)_windowHandle);
            }
            _window?.DestroyHandle();
        }
    }

    private static void Unhook(ref HWINEVENTHOOK hook)
    {
        if (hook != default)
        {
            PInvoke.UnhookWinEvent(hook);
            hook = default;
        }
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (@event == EVENT_SYSTEM_FOREGROUND)
        {
            Guard(() => HandleForeground(hwnd));
        }
        else if (@event == EVENT_OBJECT_NAMECHANGE
                 && hwnd == _foreground && idObject == OBJID_WINDOW && idChild == CHILDID_SELF
                 && _foregroundHasRule)
        {
            Guard(() => HandleTitleChange(hwnd));
        }
    }

    private void HandleForeground(HWND hwnd)
    {
        _foreground = hwnd;
        uint processId = DesktopInspector.GetProcessId(hwnd);
        WindowSnapshot snapshot = Describe(hwnd, processId, out _foregroundHasRule);

        // Title changes only matter for processes that have an allowlist rule, so the name-change
        // hook is scoped to that one process. Other processes are not observed at all.
        Unhook(ref _nameChangeHook);
        if (_foregroundHasRule && processId != 0)
        {
            _nameChangeHook = PInvoke.SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, HMODULE.Null, _winEventProc, processId, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        _lastSnapshot = snapshot;
        _onSignal(new ForegroundChanged(_time.GetUtcNow(), snapshot));
    }

    private void HandleTitleChange(HWND hwnd)
    {
        WindowSnapshot snapshot = Describe(hwnd, DesktopInspector.GetProcessId(hwnd), out _);
        if (snapshot == _lastSnapshot)
        {
            return;
        }
        _lastSnapshot = snapshot;
        _onSignal(new TitleChanged(_time.GetUtcNow(), snapshot));
    }

    /// <summary>
    /// Builds the snapshot for a window. The title is only read when the process has an allowlist
    /// rule, and both process name and title are dropped again if the window is not allowed.
    /// </summary>
    private WindowSnapshot Describe(HWND hwnd, uint processId, out bool hasRule)
    {
        hasRule = false;
        if (hwnd == default || processId == 0)
        {
            return WindowSnapshot.NonWork;
        }

        string? processName = DesktopInspector.GetProcessName(processId);
        if (processName is null || !_allowlist.HasRuleFor(processName))
        {
            return WindowSnapshot.NonWork;
        }

        hasRule = true;
        string title = DesktopInspector.GetTitle(hwnd);
        return _allowlist.IsAllowed(processName, title)
            ? WindowSnapshot.Allowed((long)(nint)hwnd, processName, title)
            : WindowSnapshot.NonWork;
    }

    private void PollInput()
    {
        TimeSpan idleFor;
        unsafe
        {
            var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
            if (!PInvoke.GetLastInputInfo(&info))
            {
                return;
            }
            // Both values are 32-bit tick counts that wrap together.
            idleFor = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
        _onSignal(new InputTick(_time.GetUtcNow(), idleFor));
    }

    private void OnWindowMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_WTSSESSION_CHANGE:
                int sessionEvent = (int)m.WParam;
                if (sessionEvent == WTS_SESSION_LOCK)
                {
                    Guard(() => _onSignal(new SessionLocked(_time.GetUtcNow())));
                }
                else if (sessionEvent == WTS_SESSION_UNLOCK)
                {
                    Guard(() => _onSignal(new SessionUnlocked(_time.GetUtcNow())));
                }
                break;

            case WM_POWERBROADCAST:
                int powerEvent = (int)m.WParam;
                if (powerEvent == PBT_APMSUSPEND)
                {
                    Guard(() => _onSignal(new SystemSuspending(_time.GetUtcNow())));
                }
                else if (powerEvent is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                {
                    Guard(() => _onSignal(new SystemResumed(_time.GetUtcNow())));
                }
                break;

            case WM_CLOSE:
                Application.ExitThread();
                break;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    /// <summary>Hidden top-level window that receives session and power notifications.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly WindowActivityMonitor _owner;

        public MessageWindow(WindowActivityMonitor owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "TimeCapture.Monitor" });
        }

        protected override void WndProc(ref Message m)
        {
            _owner.OnWindowMessage(ref m);
            base.WndProc(ref m);
        }
    }
}
