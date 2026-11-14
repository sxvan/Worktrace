using Capture.Core.Rules;

namespace Capture.Core.Monitoring;

/// <summary>
/// What the monitoring layer is allowed to tell the rest of the app about a window.
/// A window without a rule carries no handle, process name or title; a log-only window carries
/// its title only if its rule says so.
/// </summary>
public sealed record WindowSnapshot
{
    private WindowSnapshot(WindowMode mode, long handle, string? processName, string? title)
    {
        Mode = mode;
        Handle = handle;
        ProcessName = processName;
        Title = title;
    }

    public WindowMode Mode { get; }
    public long Handle { get; }
    public string? ProcessName { get; }
    public string? Title { get; }

    public static WindowSnapshot Other { get; } = new(WindowMode.Other, 0, null, null);

    public static WindowSnapshot Captured(long handle, string processName, string title) =>
        new(WindowMode.Capture, handle, processName, title);

    public static WindowSnapshot Logged(long handle, string processName, string? title) =>
        new(WindowMode.Log, handle, processName, title);
}

/// <summary>Raw observations from the operating system, in the order they happened.</summary>
public abstract record MonitorSignal(DateTimeOffset Timestamp);

public sealed record ForegroundChanged(DateTimeOffset Timestamp, WindowSnapshot Window) : MonitorSignal(Timestamp);

public sealed record TitleChanged(DateTimeOffset Timestamp, WindowSnapshot Window) : MonitorSignal(Timestamp);

/// <summary>Periodic input poll. <paramref name="IdleFor"/> is the time since the last keyboard or mouse input.</summary>
public sealed record InputTick(DateTimeOffset Timestamp, TimeSpan IdleFor) : MonitorSignal(Timestamp);

public sealed record SessionLocked(DateTimeOffset Timestamp) : MonitorSignal(Timestamp);

public sealed record SessionUnlocked(DateTimeOffset Timestamp) : MonitorSignal(Timestamp);

public sealed record SystemSuspending(DateTimeOffset Timestamp) : MonitorSignal(Timestamp);

public sealed record SystemResumed(DateTimeOffset Timestamp) : MonitorSignal(Timestamp);

/// <summary>The user paused recording. <paramref name="Until"/> is null for "until resumed".</summary>
public sealed record PauseRequested(DateTimeOffset Timestamp, DateTimeOffset? Until) : MonitorSignal(Timestamp);

/// <summary>The user resumed recording, or a timed pause ran out.</summary>
public sealed record ResumeRequested(DateTimeOffset Timestamp) : MonitorSignal(Timestamp);
