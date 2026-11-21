using Capture.Core.Export;
using Capture.Core.Monitoring;

namespace Capture.Core.Tests;

public class VisitBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static TimelineEvent Window(int minute, string process, string title) =>
        new(At(minute), EventType.Foreground, process, title);

    private static TimelineEvent Event(int minute, EventType type) => new(At(minute), type);

    [Fact]
    public void Each_event_lasts_until_the_next()
    {
        var visits = VisitBuilder.Build([Window(0, "a.exe", "A"), Window(10, "b.exe", "B")], At(0), At(30));

        Assert.Equal([(At(0), At(10), "a.exe"), (At(10), At(30), "b.exe")],
            visits.Select(v => (v.Start, v.End, v.ProcessName)));
    }

    [Fact]
    public void Idle_covers_the_window_and_the_window_comes_back_after()
    {
        var visits = VisitBuilder.Build(
            [Window(0, "a.exe", "A"), Event(5, EventType.IdleStart), Event(15, EventType.IdleEnd)], At(0), At(20));

        Assert.Equal([VisitKind.Window, VisitKind.Idle, VisitKind.Window], visits.Select(v => v.Kind));
        Assert.Equal(TimeSpan.FromMinutes(10), visits[1].Duration);
    }

    [Fact]
    public void Sleep_wins_over_idle_and_resume_clears_both()
    {
        var visits = VisitBuilder.Build(
            [Window(0, "a.exe", "A"), Event(5, EventType.IdleStart), Event(6, EventType.Sleep), Event(30, EventType.Resume)],
            At(0), At(40));

        Assert.Equal([VisitKind.Window, VisitKind.Idle, VisitKind.Asleep, VisitKind.Window], visits.Select(v => v.Kind));
    }

    [Fact]
    public void Events_before_the_range_set_the_starting_state()
    {
        var visits = VisitBuilder.Build([Window(-60, "a.exe", "A")], At(0), At(10));

        Visit visit = Assert.Single(visits);
        Assert.Equal((At(0), At(10)), (visit.Start, visit.End));
    }

    [Fact]
    public void Equal_states_in_a_row_are_merged()
    {
        var visits = VisitBuilder.Build([Window(0, "a.exe", "A"), Window(5, "a.exe", "A")], At(0), At(10));

        Assert.Single(visits);
    }

    [Fact]
    public void Visits_stop_at_the_end_of_the_data()
    {
        var visits = VisitBuilder.Build([Window(0, "a.exe", "A")], At(0), At(600), dataEnd: At(12));

        Assert.Equal(At(12), Assert.Single(visits).End);
    }

    [Fact]
    public void Out_of_order_idle_start_is_sorted_in()
    {
        // idle_start is written late but dated back to when input stopped.
        var visits = VisitBuilder.Build(
            [Window(0, "a.exe", "A"), Event(10, EventType.IdleEnd), Event(4, EventType.IdleStart)], At(0), At(12));

        Assert.Equal([(VisitKind.Window, 4.0), (VisitKind.Idle, 6.0), (VisitKind.Window, 2.0)],
            visits.Select(v => (v.Kind, v.Duration.TotalMinutes)));
    }
}
