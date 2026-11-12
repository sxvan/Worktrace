using Capture.Core.Capturing;
using Capture.Core.Geometry;

namespace Capture.Core.Tests;

public class CapturePlannerTests
{
    // Primary 2560x1440 at 100%, a 4K monitor at 150% to the left (negative coordinates),
    // and a 1920x1080 monitor at 125% above the primary.
    private static readonly MonitorInfo Primary = new(1, 11, @"\\.\DISPLAY1", new RectInt(0, 0, 2560, 1440), 1.0, true);
    private static readonly MonitorInfo Left = new(2, 22, @"\\.\DISPLAY2", new RectInt(-3840, -360, 0, 1800), 1.5, false);
    private static readonly MonitorInfo Above = new(3, 33, @"\\.\DISPLAY3", new RectInt(320, -1080, 2240, 0), 1.25, false);
    private static readonly MonitorInfo[] Monitors = [Primary, Left, Above];

    private static WindowInfo Allowed(long handle, RectInt bounds, int z) =>
        new(handle, bounds, bounds, z, IsAllowed: true, IsCovering: true, "Code.exe", "title");

    private static WindowInfo Other(long handle, RectInt bounds, int z, bool covering = true) =>
        new(handle, bounds, bounds, z, IsAllowed: false, IsCovering: covering, null, null);

    private static long Area(WindowPlacement placement) => placement.VisibleRects.Sum(r => (long)r.Width * r.Height);

    [Fact]
    public void No_allowed_window_gives_an_empty_plan()
    {
        var plan = CapturePlanner.Plan([Other(1, new RectInt(0, 0, 800, 600), 0)], Monitors);

        Assert.False(plan.HasAllowedWindows);
        Assert.Empty(plan.WindowsToCapture);
    }

    [Fact]
    public void Uncovered_allowed_window_is_fully_visible_on_its_monitor_only()
    {
        var window = Allowed(1, new RectInt(100, 100, 900, 700), 0);

        var plan = CapturePlanner.Plan([window], Monitors);

        var monitor = Assert.Single(plan.Monitors);
        Assert.Equal(Primary, monitor.Monitor);
        Assert.Equal([window.Bounds], Assert.Single(monitor.Windows).VisibleRects);
    }

    [Fact]
    public void Non_allowed_window_on_top_blacks_out_what_it_covers()
    {
        var cover = Other(1, new RectInt(500, 0, 1500, 1440), 0);
        var window = Allowed(2, new RectInt(0, 0, 1000, 1000), 1);

        var plan = CapturePlanner.Plan([cover, window], Monitors);

        var placement = Assert.Single(Assert.Single(plan.Monitors).Windows);
        Assert.Equal([new RectInt(0, 0, 500, 1000)], placement.VisibleRects);
    }

    [Fact]
    public void Window_below_an_allowed_window_does_not_cover_it()
    {
        var window = Allowed(1, new RectInt(0, 0, 1000, 1000), 0);
        var below = Other(2, new RectInt(0, 0, 2560, 1440), 1);

        var plan = CapturePlanner.Plan([window, below], Monitors);

        Assert.Equal([window.Bounds], Assert.Single(Assert.Single(plan.Monitors).Windows).VisibleRects);
    }

    [Fact]
    public void Allowed_windows_cover_each_other_so_no_pixel_is_drawn_twice()
    {
        var top = Allowed(1, new RectInt(200, 200, 800, 800), 0);
        var bottom = Allowed(2, new RectInt(0, 0, 1000, 1000), 1);

        var plan = CapturePlanner.Plan([top, bottom], Monitors);

        var windows = Assert.Single(plan.Monitors).Windows;
        Assert.Equal([1L, 2L], windows.Select(w => w.Window.Handle));
        Assert.Equal(600 * 600, Area(windows[0]));
        Assert.Equal(1000 * 1000 - 600 * 600, Area(windows[1]));
        Assert.All(windows[1].VisibleRects, r => Assert.True(r.Intersect(top.Bounds).IsEmpty));
    }

    [Fact]
    public void Fully_covered_allowed_window_is_not_captured()
    {
        var cover = Other(1, new RectInt(0, 0, 2560, 1440), 0);
        var window = Allowed(2, new RectInt(100, 100, 900, 700), 1);

        var plan = CapturePlanner.Plan([cover, window], Monitors);

        Assert.False(plan.HasAllowedWindows);
    }

    [Fact]
    public void Click_through_overlay_does_not_cover_anything()
    {
        var overlay = Other(1, new RectInt(0, 0, 2560, 1440), 0, covering: false);
        var window = Allowed(2, new RectInt(100, 100, 900, 700), 1);

        var plan = CapturePlanner.Plan([overlay, window], Monitors);

        Assert.Equal([window.Bounds], Assert.Single(Assert.Single(plan.Monitors).Windows).VisibleRects);
    }

    [Fact]
    public void Window_on_a_monitor_with_negative_coordinates()
    {
        var window = Allowed(1, new RectInt(-3000, -100, -1000, 900), 0);
        var cover = Other(2, new RectInt(-2000, 400, -500, 1200), 0);

        var plan = CapturePlanner.Plan([cover, window with { ZIndex = 1 }], Monitors);

        var monitor = Assert.Single(plan.Monitors);
        Assert.Equal(Left, monitor.Monitor);
        var placement = Assert.Single(monitor.Windows);
        Assert.Equal(2000L * 1000 - 1000 * 500, Area(placement));
        Assert.All(placement.VisibleRects, r => Assert.True(Left.Bounds.Contains(r)));
    }

    [Fact]
    public void Window_spanning_two_monitors_with_different_scaling_is_split_at_the_edge()
    {
        // Straddles the border between the 150% monitor on the left and the 100% primary.
        var window = Allowed(1, new RectInt(-600, 100, 400, 900), 0);

        var plan = CapturePlanner.Plan([window], Monitors);

        Assert.Equal(2, plan.Monitors.Count);
        Assert.Single(plan.WindowsToCapture);
        var onPrimary = plan.Monitors.Single(m => m.Monitor == Primary).Windows.Single();
        var onLeft = plan.Monitors.Single(m => m.Monitor == Left).Windows.Single();
        Assert.Equal([new RectInt(0, 100, 400, 900)], onPrimary.VisibleRects);
        Assert.Equal([new RectInt(-600, 100, 0, 900)], onLeft.VisibleRects);
    }

    [Fact]
    public void Window_partly_off_all_monitors_is_clipped()
    {
        var window = Allowed(1, new RectInt(2000, 1000, 3000, 2000), 0);

        var plan = CapturePlanner.Plan([window], Monitors);

        Assert.Equal([new RectInt(2000, 1000, 2560, 1440)], plan.Monitors.Single().Windows.Single().VisibleRects);
    }

    [Fact]
    public void Monitor_above_the_primary_gets_its_own_plan()
    {
        var onPrimary = Allowed(1, new RectInt(0, 0, 500, 500), 0);
        var onAbove = Allowed(2, new RectInt(400, -900, 1200, -100), 1);

        var plan = CapturePlanner.Plan([onPrimary, onAbove], Monitors);

        Assert.Equal([Primary, Above], plan.Monitors.Select(m => m.Monitor));
        Assert.Equal(2, plan.WindowsToCapture.Count);
    }

    // ---- Consistency check ----

    private static CapturePlan PlanOf(params WindowInfo[] windows) => CapturePlanner.Plan(windows, Monitors);

    [Fact]
    public void Identical_layouts_match()
    {
        var a = PlanOf(Other(9, new RectInt(0, 0, 300, 300), 0), Allowed(1, new RectInt(0, 0, 1000, 1000), 1));
        var b = PlanOf(Other(9, new RectInt(0, 0, 300, 300), 0), Allowed(1, new RectInt(0, 0, 1000, 1000), 1));

        Assert.True(a.HasSameLayoutAs(b));
    }

    [Fact]
    public void Moved_or_resized_allowed_window_is_a_mismatch()
    {
        var before = PlanOf(Allowed(1, new RectInt(0, 0, 1000, 1000), 0));

        Assert.False(before.HasSameLayoutAs(PlanOf(Allowed(1, new RectInt(10, 0, 1010, 1000), 0))));
        Assert.False(before.HasSameLayoutAs(PlanOf(Allowed(1, new RectInt(0, 0, 1000, 900), 0))));
    }

    [Fact]
    public void Appearing_or_disappearing_allowed_window_is_a_mismatch()
    {
        var one = PlanOf(Allowed(1, new RectInt(0, 0, 500, 500), 0));
        var two = PlanOf(Allowed(1, new RectInt(0, 0, 500, 500), 0), Allowed(2, new RectInt(600, 0, 900, 500), 1));

        Assert.False(one.HasSameLayoutAs(two));
        Assert.False(two.HasSameLayoutAs(one));
        Assert.False(one.HasSameLayoutAs(PlanOf()));
    }

    [Fact]
    public void Changed_z_order_of_allowed_windows_is_a_mismatch()
    {
        var a = Allowed(1, new RectInt(0, 0, 500, 500), 0);
        var b = Allowed(2, new RectInt(250, 250, 750, 750), 1);

        var before = PlanOf(a, b);
        var after = PlanOf(b with { ZIndex = 0 }, a with { ZIndex = 1 });

        Assert.False(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Non_allowed_window_moving_over_an_allowed_one_is_a_mismatch()
    {
        var window = Allowed(1, new RectInt(0, 0, 1000, 1000), 1);

        var before = PlanOf(Other(9, new RectInt(1500, 0, 2000, 500), 0), window);
        var after = PlanOf(Other(9, new RectInt(800, 0, 1300, 500), 0), window);

        Assert.False(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Non_allowed_window_moving_elsewhere_does_not_matter()
    {
        var window = Allowed(1, new RectInt(0, 0, 1000, 1000), 1);

        var before = PlanOf(Other(9, new RectInt(1500, 0, 2000, 500), 0), window);
        var after = PlanOf(Other(9, new RectInt(1600, 100, 2100, 600), 0), window);

        Assert.True(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Changed_monitor_setup_is_a_mismatch()
    {
        WindowInfo[] windows = [Allowed(1, new RectInt(0, 0, 500, 500), 0)];

        var before = CapturePlanner.Plan(windows, [Primary]);
        var after = CapturePlanner.Plan(windows, [Primary with { Bounds = new RectInt(0, 0, 1920, 1080) }]);

        Assert.False(before.HasSameLayoutAs(after));
    }
}
