using Capture.Core.Export;
using Capture.Core.Monitoring;
using Capture.Core.Redaction;

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

    // ---- ExportFormatter ----

    [Fact]
    public void Text_is_attached_to_the_visit_of_the_same_process()
    {
        Visit[] visits = [new(At(0), At(10), VisitKind.Window, "a.exe", "A"), new(At(10), At(20), VisitKind.Window, "b.exe", "B")];
        TextSample[] samples =
        [
            new(At(1), 1, "a.exe", [new ScreenTextLine("alpha text", 0.3, 0.3, 0.6, 0.32)]),
            new(At(2), 2, "a.exe", [new ScreenTextLine("alpha text", 0.3, 0.3, 0.6, 0.32), new ScreenTextLine("EXPLORER", 0.0, 0.1, 0.1, 0.12)]),
            new(At(11), 3, "b.exe", [new ScreenTextLine("ab", 0.3, 0.3, 0.6, 0.32)]),
        ];

        var export = ExportFormatter.Assemble(visits, samples, [new ImageRef(At(1), 1, "a.exe")], new ExportOptions(), TextScrubber.Default);

        Assert.Equal([("main", "alpha text", 2), ("left", "EXPLORER", 1)], export[0].Text.Select(t => (t.Region, t.Text, t.Count)));
        Assert.Empty(export[1].Text); // "ab" is too short to say anything
        Assert.Equal([1L], export[0].ImageIds);
    }

    [Fact]
    public void Layer_1_has_no_text()
    {
        Visit[] visits = [new(At(0), At(10), VisitKind.Window, "a.exe", "A")];
        TextSample[] samples = [new(At(1), 1, "a.exe", [new ScreenTextLine("alpha text", 0.3, 0.3, 0.6, 0.32)])];

        var export = ExportFormatter.Assemble(visits, samples, [], new ExportOptions(ExportLayer.Titles), TextScrubber.Default);

        Assert.Empty(export[0].Text);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(125, "2m05s")]
    [InlineData(3900, "1h05m")]
    public void Durations_are_short(int seconds, string expected)
    {
        Assert.Equal(expected, ExportFormatter.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
