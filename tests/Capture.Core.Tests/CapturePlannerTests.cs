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

    private static WindowInfo Capturable(long handle, RectInt bounds, int z) =>
        new(handle, bounds, bounds, z, IsCapturable: true, IsCovering: true, "Code.exe", "title");

    private static WindowInfo Other(long handle, RectInt bounds, int z, bool covering = true) =>
        new(handle, bounds, bounds, z, IsCapturable: false, IsCovering: covering, null, null);

    private static long Area(WindowPlacement placement) => placement.VisibleRects.Sum(r => (long)r.Width * r.Height);

    [Fact]
    public void No_capturable_window_gives_an_empty_plan()
    {
        var plan = CapturePlanner.Plan([Other(1, new RectInt(0, 0, 800, 600), 0)], Monitors);

        Assert.False(plan.HasCapturableWindows);
    }

    [Fact]
    public void Uncovered_capturable_window_is_fully_visible()
    {
        var window = Capturable(1, new RectInt(100, 100, 900, 700), 0);

        var placement = Assert.Single(CapturePlanner.Plan([window], Monitors).Windows);

        Assert.Equal(Primary, placement.Monitor);
        Assert.Equal([window.Bounds], placement.VisibleRects);
        Assert.Equal(1.0, placement.VisibleFraction);
    }

    [Fact]
    public void Other_window_on_top_blacks_out_what_it_covers()
    {
        var cover = Other(1, new RectInt(500, 0, 1500, 1440), 0);
        var window = Capturable(2, new RectInt(0, 0, 1000, 1000), 1);

        var placement = Assert.Single(CapturePlanner.Plan([cover, window], Monitors).Windows);

        Assert.Equal([new RectInt(0, 0, 500, 1000)], placement.VisibleRects);
        Assert.Equal(0.5, placement.VisibleFraction);
    }

    [Fact]
    public void Window_below_an_capturable_window_does_not_cover_it()
    {
        var window = Capturable(1, new RectInt(0, 0, 1000, 1000), 0);
        var below = Other(2, new RectInt(0, 0, 2560, 1440), 1);

        var placement = Assert.Single(CapturePlanner.Plan([window, below], Monitors).Windows);

        Assert.Equal([window.Bounds], placement.VisibleRects);
    }

    [Fact]
    public void Capturable_windows_cover_each_other()
    {
        var top = Capturable(1, new RectInt(200, 200, 800, 800), 0);
        var bottom = Capturable(2, new RectInt(0, 0, 1000, 1000), 1);

        var windows = CapturePlanner.Plan([top, bottom], Monitors).Windows;

        Assert.Equal([1L, 2L], windows.Select(w => w.Window.Handle));
        Assert.Equal(600 * 600, Area(windows[0]));
        Assert.Equal(1000 * 1000 - 600 * 600, Area(windows[1]));
        Assert.All(windows[1].VisibleRects, r => Assert.True(r.Intersect(top.Bounds).IsEmpty));
    }

    [Fact]
    public void Fully_covered_capturable_window_is_not_captured()
    {
        var cover = Other(1, new RectInt(0, 0, 2560, 1440), 0);
        var window = Capturable(2, new RectInt(100, 100, 900, 700), 1);

        Assert.False(CapturePlanner.Plan([cover, window], Monitors).HasCapturableWindows);
    }

    [Fact]
    public void Click_through_overlay_does_not_cover_anything()
    {
        var overlay = Other(1, new RectInt(0, 0, 2560, 1440), 0, covering: false);
        var window = Capturable(2, new RectInt(100, 100, 900, 700), 1);

        var placement = Assert.Single(CapturePlanner.Plan([overlay, window], Monitors).Windows);

        Assert.Equal([window.Bounds], placement.VisibleRects);
    }

    [Fact]
    public void Window_on_a_monitor_with_negative_coordinates()
    {
        var cover = Other(2, new RectInt(-2000, 400, -500, 1200), 0);
        var window = Capturable(1, new RectInt(-3000, -100, -1000, 900), 1);

        var placement = Assert.Single(CapturePlanner.Plan([cover, window], Monitors).Windows);

        Assert.Equal(Left, placement.Monitor);
        Assert.Equal(2000L * 1000 - 1000 * 500, Area(placement));
        Assert.All(placement.VisibleRects, r => Assert.True(Left.Bounds.Contains(r)));
    }

    [Fact]
    public void Window_spanning_two_monitors_with_different_scaling_is_one_window_with_both_parts()
    {
        // Straddles the border between the 150% monitor on the left and the 100% primary.
        var window = Capturable(1, new RectInt(-600, 100, 400, 900), 0);

        var placement = Assert.Single(CapturePlanner.Plan([window], Monitors).Windows);

        Assert.Equal(Left, placement.Monitor); // the larger part is on the left monitor
        Assert.Equal(
            new HashSet<RectInt> { new(-600, 100, 0, 900), new(0, 100, 400, 900) },
            placement.VisibleRects.ToHashSet());
        Assert.Equal(1.0, placement.VisibleFraction);
        Assert.Equal(window.Bounds, placement.VisibleBounds);
    }

    [Fact]
    public void Window_partly_off_all_monitors_is_clipped()
    {
        var window = Capturable(1, new RectInt(2000, 1000, 3000, 2000), 0);

        var placement = Assert.Single(CapturePlanner.Plan([window], Monitors).Windows);

        Assert.Equal([new RectInt(2000, 1000, 2560, 1440)], placement.VisibleRects);
    }

    [Fact]
    public void Windows_on_different_monitors_are_assigned_to_them()
    {
        var onPrimary = Capturable(1, new RectInt(0, 0, 500, 500), 0);
        var onAbove = Capturable(2, new RectInt(400, -900, 1200, -100), 1);

        var windows = CapturePlanner.Plan([onPrimary, onAbove], Monitors).Windows;

        Assert.Equal([Primary, Above], windows.Select(w => w.Monitor));
    }

    // ---- Consistency check ----

    private static CapturePlan PlanOf(params WindowInfo[] windows) => CapturePlanner.Plan(windows, Monitors);

    [Fact]
    public void Identical_layouts_match()
    {
        var a = PlanOf(Other(9, new RectInt(0, 0, 300, 300), 0), Capturable(1, new RectInt(0, 0, 1000, 1000), 1));
        var b = PlanOf(Other(9, new RectInt(0, 0, 300, 300), 0), Capturable(1, new RectInt(0, 0, 1000, 1000), 1));

        Assert.True(a.HasSameLayoutAs(b));
    }

    [Fact]
    public void Moved_or_resized_capturable_window_is_a_mismatch()
    {
        var before = PlanOf(Capturable(1, new RectInt(0, 0, 1000, 1000), 0));

        Assert.False(before.HasSameLayoutAs(PlanOf(Capturable(1, new RectInt(10, 0, 1010, 1000), 0))));
        Assert.False(before.HasSameLayoutAs(PlanOf(Capturable(1, new RectInt(0, 0, 1000, 900), 0))));
    }

    [Fact]
    public void Appearing_or_disappearing_capturable_window_is_a_mismatch()
    {
        var one = PlanOf(Capturable(1, new RectInt(0, 0, 500, 500), 0));
        var two = PlanOf(Capturable(1, new RectInt(0, 0, 500, 500), 0), Capturable(2, new RectInt(600, 0, 900, 500), 1));

        Assert.False(one.HasSameLayoutAs(two));
        Assert.False(two.HasSameLayoutAs(one));
        Assert.False(one.HasSameLayoutAs(PlanOf()));
    }

    [Fact]
    public void Changed_z_order_of_capturable_windows_is_a_mismatch()
    {
        var a = Capturable(1, new RectInt(0, 0, 500, 500), 0);
        var b = Capturable(2, new RectInt(250, 250, 750, 750), 1);

        var before = PlanOf(a, b);
        var after = PlanOf(b with { ZIndex = 0 }, a with { ZIndex = 1 });

        Assert.False(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Other_window_moving_over_an_capturable_one_is_a_mismatch()
    {
        var window = Capturable(1, new RectInt(0, 0, 1000, 1000), 1);

        var before = PlanOf(Other(9, new RectInt(1500, 0, 2000, 500), 0), window);
        var after = PlanOf(Other(9, new RectInt(800, 0, 1300, 500), 0), window);

        Assert.False(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Other_window_moving_elsewhere_does_not_matter()
    {
        var window = Capturable(1, new RectInt(0, 0, 1000, 1000), 1);

        var before = PlanOf(Other(9, new RectInt(1500, 0, 2000, 500), 0), window);
        var after = PlanOf(Other(9, new RectInt(1600, 100, 2100, 600), 0), window);

        Assert.True(before.HasSameLayoutAs(after));
    }

    [Fact]
    public void Changed_monitor_setup_is_a_mismatch()
    {
        WindowInfo[] windows = [Capturable(1, new RectInt(0, 0, 500, 500), 0)];

        var before = CapturePlanner.Plan(windows, [Primary]);
        var resized = CapturePlanner.Plan(windows, [Primary with { Bounds = new RectInt(0, 0, 1920, 1080) }]);
        var added = CapturePlanner.Plan(windows, [Primary, Left]);

        Assert.False(before.HasSameLayoutAs(resized));
        Assert.False(before.HasSameLayoutAs(added));
    }
}
