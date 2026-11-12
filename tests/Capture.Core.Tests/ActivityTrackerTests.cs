using Capture.Core.Monitoring;

namespace Capture.Core.Tests;

public class ActivityTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly WindowSnapshot Editor = WindowSnapshot.Captured(100, "Code.exe", "a.cs");
    private static readonly WindowSnapshot EditorOtherFile = WindowSnapshot.Captured(100, "Code.exe", "b.cs");

    private static ActivityTracker Tracker() => new(TimeSpan.FromMinutes(3));

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Allowed_foreground_window_is_logged_with_details()
    {
        var e = Assert.Single(Tracker().Process(new ForegroundChanged(T0, Editor)));

        Assert.Equal(new ActivityEvent(T0, EventType.Foreground, "Code.exe", "a.cs", 100), e);
    }

    [Fact]
    public void Non_allowed_window_is_logged_as_non_work_without_details()
    {
        var e = Assert.Single(Tracker().Process(new ForegroundChanged(T0, WindowSnapshot.Other)));

        Assert.Equal(new ActivityEvent(T0, EventType.Other), e);
    }

    [Fact]
    public void Switching_between_non_work_windows_logs_only_once()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), WindowSnapshot.Other));

        Assert.Empty(tracker.Process(new ForegroundChanged(At(5), WindowSnapshot.Other)));
    }

    [Fact]
    public void Repeated_foreground_signal_for_same_window_is_ignored()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));

        Assert.Empty(tracker.Process(new ForegroundChanged(At(1), Editor)));
    }

    [Fact]
    public void Title_change_is_logged()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));

        var e = Assert.Single(tracker.Process(new TitleChanged(At(2), EditorOtherFile)));

        Assert.Equal(EventType.TitleChange, e.Type);
        Assert.Equal("b.cs", e.WindowTitle);
    }

    [Fact]
    public void Title_change_that_makes_window_non_allowed_logs_non_work()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));

        var e = Assert.Single(tracker.Process(new TitleChanged(At(2), WindowSnapshot.Other)));

        Assert.Equal(new ActivityEvent(At(2), EventType.Other), e);
    }

    [Fact]
    public void Idle_starts_at_last_input_and_ends_at_next_input()
    {
        var tracker = Tracker();

        Assert.Empty(tracker.Process(new InputTick(At(100), TimeSpan.FromSeconds(179))));

        var start = Assert.Single(tracker.Process(new InputTick(At(101), TimeSpan.FromSeconds(180))));
        Assert.Equal(new ActivityEvent(At(101 - 180), EventType.IdleStart), start);
        Assert.True(tracker.IsIdle);

        Assert.Empty(tracker.Process(new InputTick(At(102), TimeSpan.FromSeconds(181))));

        var end = Assert.Single(tracker.Process(new InputTick(At(300), TimeSpan.FromMilliseconds(400))));
        Assert.Equal(EventType.IdleEnd, end.Type);
        Assert.Equal(At(300) - TimeSpan.FromMilliseconds(400), end.Timestamp);
        Assert.False(tracker.IsIdle);
    }

    [Fact]
    public void Lock_and_unlock_are_logged_once_and_foreground_is_restated()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));

        Assert.Equal(EventType.Lock, Assert.Single(tracker.Process(new SessionLocked(At(10)))).Type);
        Assert.Empty(tracker.Process(new SessionLocked(At(11))));
        Assert.True(tracker.IsSuspended);

        var events = tracker.Process(new SessionUnlocked(At(60)));

        Assert.Equal([EventType.Unlock, EventType.Foreground], events.Select(e => e.Type));
        Assert.Equal("a.cs", events[1].WindowTitle);
        Assert.False(tracker.IsSuspended);
    }

    [Fact]
    public void Nothing_is_logged_while_locked()
    {
        var tracker = Tracker();
        tracker.Process(new SessionLocked(At(0)));

        Assert.Empty(tracker.Process(new ForegroundChanged(At(1), WindowSnapshot.Other)));
        Assert.Empty(tracker.Process(new InputTick(At(400), TimeSpan.FromSeconds(399))));
        Assert.False(tracker.IsIdle);
    }

    [Fact]
    public void Window_change_during_lock_is_reported_after_unlock()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));
        tracker.Process(new SessionLocked(At(1)));
        tracker.Process(new ForegroundChanged(At(2), WindowSnapshot.Other));

        var events = tracker.Process(new SessionUnlocked(At(3)));

        Assert.Equal([EventType.Unlock, EventType.Other], events.Select(e => e.Type));
    }

    [Fact]
    public void Sleep_and_resume_are_logged_once()
    {
        var tracker = Tracker();

        Assert.Equal(EventType.Sleep, Assert.Single(tracker.Process(new SystemSuspending(At(0)))).Type);
        Assert.Empty(tracker.Process(new SystemSuspending(At(1))));
        Assert.Equal(EventType.Resume, Assert.Single(tracker.Process(new SystemResumed(At(100)))).Type);
        // Windows often sends two resume notifications.
        Assert.Empty(tracker.Process(new SystemResumed(At(101))));
    }

    [Fact]
    public void Resume_while_still_locked_does_not_restate_foreground()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), Editor));
        tracker.Process(new SystemSuspending(At(1)));
        tracker.Process(new SessionLocked(At(2)));

        Assert.Equal([EventType.Resume], tracker.Process(new SystemResumed(At(50))).Select(e => e.Type));
        Assert.Equal([EventType.Unlock, EventType.Foreground], tracker.Process(new SessionUnlocked(At(60))).Select(e => e.Type));
    }

    [Fact]
    public void Log_only_window_is_logged_with_process_but_without_title()
    {
        var e = Assert.Single(Tracker().Process(new ForegroundChanged(T0, WindowSnapshot.Logged(7, "keepass.exe", null))));

        Assert.Equal(new ActivityEvent(T0, EventType.Foreground, "keepass.exe", null, 7), e);
    }

    [Fact]
    public void Log_only_window_with_titles_logs_title_changes()
    {
        var tracker = Tracker();
        tracker.Process(new ForegroundChanged(At(0), WindowSnapshot.Logged(7, "msedge.exe", "Docs")));

        var e = Assert.Single(tracker.Process(new TitleChanged(At(3), WindowSnapshot.Logged(7, "msedge.exe", "Mail"))));

        Assert.Equal(new ActivityEvent(At(3), EventType.TitleChange, "msedge.exe", "Mail", 7), e);
    }
}