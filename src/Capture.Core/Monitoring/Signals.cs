namespace Capture.Core.Monitoring;

/// <summary>
/// What the monitoring layer is allowed to tell the rest of the app about a window.
/// A window that is not on the allowlist carries no handle, process name or title.
/// </summary>
public sealed record WindowSnapshot
{
    private WindowSnapshot(bool isAllowed, long handle, string? processName, string? title)
    {
        IsAllowed = isAllowed;
        Handle = handle;
        ProcessName = processName;
        Title = title;
    }

    public bool IsAllowed { get; }
    public long Handle { get; }
    public string? ProcessName { get; }
    public string? Title { get; }

    public static WindowSnapshot NonWork { get; } = new(false, 0, null, null);

    public static WindowSnapshot Allowed(long handle, string processName, string title) =>
        new(true, handle, processName, title);
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
