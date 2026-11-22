using Capture.Core.Geometry;

namespace Capture.Core.Capturing;

/// <summary>
/// A visible top-level window. Process name and title are only present for capturable windows;
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
    bool IsCapturable,
    bool IsCovering,
    string? ProcessName,
    string? Title);

/// <param name="Bounds">The monitor's area in virtual-screen coordinates.</param>
public sealed record MonitorInfo(long Handle, RectInt Bounds);

/// <summary>An capturable window and the parts of it that are actually visible on screen.</summary>
/// <param name="VisibleRects">Non-overlapping rectangles in virtual-screen coordinates.</param>
public sealed record WindowPlacement(WindowInfo Window, IReadOnlyList<RectInt> VisibleRects);

/// <summary>What one capture has to record: the capturable windows that are visible, topmost first.</summary>
public sealed class CapturePlan(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowPlacement> windows)
{
    public IReadOnlyList<MonitorInfo> Monitors { get; } = monitors;

    public IReadOnlyList<WindowPlacement> Windows { get; } = windows;

    public bool HasCapturableWindows => Windows.Count > 0;

    /// <summary>
    /// True if the other plan would produce the same pictures: same monitors, same capturable windows
    /// in the same order with the same bounds and the same visible parts. Used to detect windows
    /// that moved, resized, changed z-order, appeared or disappeared while capturing.
    /// </summary>
    public bool HasSameLayoutAs(CapturePlan other)
    {
        if (Monitors.Count != other.Monitors.Count || Windows.Count != other.Windows.Count)
        {
            return false;
        }

        for (int m = 0; m < Monitors.Count; m++)
        {
            if (Monitors[m].Handle != other.Monitors[m].Handle || Monitors[m].Bounds != other.Monitors[m].Bounds)
            {
                return false;
            }
        }

        for (int w = 0; w < Windows.Count; w++)
        {
            WindowPlacement a = Windows[w], b = other.Windows[w];
            if (a.Window.Handle != b.Window.Handle
                || a.Window.Bounds != b.Window.Bounds
                || a.Window.WindowRect != b.Window.WindowRect
                || !a.VisibleRects.SequenceEqual(b.VisibleRects))
            {
                return false;
            }
        }

        return true;
    }
}

public static class CapturePlanner
{
    /// <summary>
    /// Works out which parts of which capturable windows are visible. A window's visible region is its
    /// bounds, clipped to the monitors, minus everything above it in z-order, whether that is
    /// capturable or not.
    /// </summary>
    /// <param name="windows">Visible top-level windows, topmost first.</param>
    public static CapturePlan Plan(IReadOnlyList<WindowInfo> windows, IReadOnlyList<MonitorInfo> monitors)
    {
        var placements = new List<WindowPlacement>();

        for (int i = 0; i < windows.Count; i++)
        {
            WindowInfo window = windows[i];
            if (!window.IsCapturable || window.Bounds.IsEmpty)
            {
                continue;
            }

            var covers = windows.Take(i).Where(above => above.IsCovering).Select(above => above.Bounds).ToList();
            var visible = new List<RectInt>();
            foreach (MonitorInfo monitor in monitors)
            {
                visible.AddRange(Region.Subtract(window.Bounds.Intersect(monitor.Bounds), covers));
            }

            // A window hidden completely, or off every monitor, has nothing to capture.
            if (visible.Any(r => !r.IsEmpty))
            {
                placements.Add(new WindowPlacement(window, visible));
            }
        }

        return new CapturePlan(monitors, placements);
    }
}
