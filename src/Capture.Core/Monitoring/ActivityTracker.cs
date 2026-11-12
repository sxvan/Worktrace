using Capture.Core.Allowlist;

namespace Capture.Core.Monitoring;

/// <summary>
/// Turns raw monitor signals into the events that get stored. Pure logic, not thread-safe:
/// feed it from a single consumer.
/// </summary>
public sealed class ActivityTracker(TimeSpan idleTimeout)
{
    private bool _locked;
    private bool _asleep;
    private bool _idle;
    private WindowSnapshot? _current;
    private WindowSnapshot? _lastLogged;

    public bool IsIdle => _idle;

    /// <summary>Locked or asleep: nothing is observed or captured.</summary>
    public bool IsSuspended => _locked || _asleep;

    /// <summary>The current foreground window as far as it may be known, or null before the first signal.</summary>
    public WindowSnapshot? CurrentWindow => _current;

    public IReadOnlyList<ActivityEvent> Process(MonitorSignal signal)
    {
        var events = new List<ActivityEvent>(2);

        switch (signal)
        {
            case ForegroundChanged s:
                _current = s.Window;
                if (!IsSuspended)
                {
                    LogWindow(events, s.Timestamp, s.Window, EventType.Foreground);
                }
                break;

            case TitleChanged s:
                _current = s.Window;
                if (!IsSuspended)
                {
                    LogWindow(events, s.Timestamp, s.Window, EventType.TitleChange);
                }
                break;

            case InputTick s:
                if (IsSuspended)
                {
                    break;
                }
                if (!_idle && s.IdleFor >= idleTimeout)
                {
                    _idle = true;
                    // The idle period began with the last input, not when the timeout was noticed.
                    events.Add(new ActivityEvent(s.Timestamp - s.IdleFor, EventType.IdleStart));
                }
                else if (_idle && s.IdleFor < idleTimeout)
                {
                    _idle = false;
                    events.Add(new ActivityEvent(s.Timestamp - s.IdleFor, EventType.IdleEnd));
                }
                break;

            case SessionLocked s:
                if (!_locked)
                {
                    _locked = true;
                    events.Add(new ActivityEvent(s.Timestamp, EventType.Lock));
                }
                break;

            case SessionUnlocked s:
                if (_locked)
                {
                    _locked = false;
                    events.Add(new ActivityEvent(s.Timestamp, EventType.Unlock));
                    RelogCurrentWindow(events, s.Timestamp);
                }
                break;

            case SystemSuspending s:
                if (!_asleep)
                {
                    _asleep = true;
                    events.Add(new ActivityEvent(s.Timestamp, EventType.Sleep));
                }
                break;

            case SystemResumed s:
                if (_asleep)
                {
                    _asleep = false;
                    events.Add(new ActivityEvent(s.Timestamp, EventType.Resume));
                    RelogCurrentWindow(events, s.Timestamp);
                }
                break;
        }

        return events;
    }

    /// <summary>After a break, state the foreground window again so the timeline is self-contained.</summary>
    private void RelogCurrentWindow(List<ActivityEvent> events, DateTimeOffset timestamp)
    {
        if (IsSuspended || _current is null)
        {
            return;
        }
        _lastLogged = null;
        LogWindow(events, timestamp, _current, EventType.Foreground);
    }

    private void LogWindow(List<ActivityEvent> events, DateTimeOffset timestamp, WindowSnapshot window, EventType typeIfAllowed)
    {
        // Equal snapshots (same window, same title, or "other" after "other") add no information.
        if (window == _lastLogged)
        {
            return;
        }
        _lastLogged = window;

        events.Add(window.Mode != WindowMode.Other
            ? new ActivityEvent(timestamp, typeIfAllowed, window.ProcessName, window.Title, window.Handle)
            : new ActivityEvent(timestamp, EventType.Other));
    }
}
