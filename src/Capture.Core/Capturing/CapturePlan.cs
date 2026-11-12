using Capture.Core.Geometry;

namespace Capture.Core.Capturing;

/// <summary>
/// A visible top-level window. Process name and title are only present for allowed windows;
/// for all others just the geometry is kept, because it is needed to work out what they cover.
/// </summary>
/// <param name="Bounds">Visible frame bounds (DWM extended frame bounds).</param>
/// <param name="WindowRect">GetWindowRect, which can include invisible resize borders.</param>
/// <param name="ZIndex">Position in z-order, 0 is topmost.</param>
/// <param name="IsCovering">False for click-through overlay windows that hide nothing.</param>
public sealed record WindowInfo(
    long Handle,
    RectInt Bounds,
    RectInt WindowRect,
    int ZIndex,
    bool IsAllowed,
    bool IsCovering,
    string? ProcessName,
    string? Title);

/// <param name="Index">1-based: the primary monitor is 1, the others follow left to right, top to bottom.</param>
/// <param name="Scale">Display scaling, 1.0 = 100%.</param>
public sealed record MonitorInfo(int Index, long Handle, string DeviceName, RectInt Bounds, double Scale, bool IsPrimary);

/// <summary>An allowed window on one monitor and the parts of it that are actually visible there.</summary>
public sealed record WindowPlacement(WindowInfo Window, IReadOnlyList<RectInt> VisibleRects)
{
    public RectInt VisibleBounds => new(
        VisibleRects.Min(r => r.Left), VisibleRects.Min(r => r.Top),
        VisibleRects.Max(r => r.Right), VisibleRects.Max(r => r.Bottom));
}

/// <summary>The allowed windows visible on one monitor, topmost first.</summary>
public sealed record MonitorPlan(MonitorInfo Monitor, IReadOnlyList<WindowPlacement> Windows);

/// <summary>What one capture has to record: only monitors that show at least one allowed window.</summary>
public sealed class CapturePlan(IReadOnlyList<MonitorPlan> monitors)
{
    public IReadOnlyList<MonitorPlan> Monitors { get; } = monitors;

    public bool HasAllowedWindows => Monitors.Count > 0;

    /// <summary>Each allowed window that is visible somewhere, once.</summary>
    public IReadOnlyList<WindowInfo> WindowsToCapture { get; } = monitors
        .SelectMany(m => m.Windows)
        .Select(p => p.Window)
        .DistinctBy(w => w.Handle)
        .ToList();

    /// <summary>
    /// True if the other plan would produce the same picture: same monitors, same allowed windows
    /// in the same order with the same bounds and the same visible parts. Used to detect windows
    /// that moved, resized, changed z-order, appeared or disappeared while capturing.
    /// </summary>
    public bool HasSameLayoutAs(CapturePlan other)
    {
        if (Monitors.Count != other.Monitors.Count)
        {
            return false;
        }

        for (int m = 0; m < Monitors.Count; m++)
        {
            MonitorPlan a = Monitors[m], b = other.Monitors[m];
            if (a.Monitor.Handle != b.Monitor.Handle || a.Monitor.Bounds != b.Monitor.Bounds || a.Windows.Count != b.Windows.Count)
            {
                return false;
            }

            for (int w = 0; w < a.Windows.Count; w++)
            {
                WindowPlacement pa = a.Windows[w], pb = b.Windows[w];
                if (pa.Window.Handle != pb.Window.Handle
                    || pa.Window.Bounds != pb.Window.Bounds
                    || pa.Window.WindowRect != pb.Window.WindowRect
                    || !pa.VisibleRects.SequenceEqual(pb.VisibleRects))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

public static class CapturePlanner
{
    /// <summary>
    /// Works out, per monitor, which parts of which allowed windows are visible.
    /// A window's visible region is its bounds, clipped to the monitor, minus everything above it
    /// in z-order, whether that is allowed or not.
    /// </summary>
    /// <param name="windows">Visible top-level windows, topmost first.</param>
    public static CapturePlan Plan(IReadOnlyList<WindowInfo> windows, IReadOnlyList<MonitorInfo> monitors)
    {
        var plans = new List<MonitorPlan>();

        foreach (MonitorInfo monitor in monitors)
        {
            var placements = new List<WindowPlacement>();

            for (int i = 0; i < windows.Count; i++)
            {
                WindowInfo window = windows[i];
                if (!window.IsAllowed)
                {
                    continue;
                }

                RectInt onMonitor = window.Bounds.Intersect(monitor.Bounds);
                if (onMonitor.IsEmpty)
                {
                    continue;
                }

                List<RectInt> visible = Region.Subtract(
                    onMonitor,
                    windows.Take(i).Where(above => above.IsCovering).Select(above => above.Bounds));
                if (visible.Count > 0)
                {
                    placements.Add(new WindowPlacement(window, visible));
                }
            }

            if (placements.Count > 0)
            {
                plans.Add(new MonitorPlan(monitor, placements));
            }
        }

        return new CapturePlan(plans);
    }
}
