using Capture.Core.Triggers;

namespace Capture.Core.Tests;

public class CaptureTriggerEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly TriggerSettings Settings = new(
        WindowSwitchDelay: TimeSpan.FromSeconds(1),
        TypingPause: TimeSpan.FromSeconds(2),
        FallbackInterval: TimeSpan.FromSeconds(60),
        MinCaptureInterval: TimeSpan.FromSeconds(5),
        IdleTimeout: TimeSpan.FromSeconds(180));

    private static CaptureTriggerEngine Engine() => new(Settings);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Feeds one tick per second from <paramref name="from"/> to <paramref name="to"/> with the user typing continuously.</summary>
    private static void TypeContinuously(CaptureTriggerEngine engine, double from, double to)
    {
        for (double t = from; t <= to; t++)
        {
            engine.OnInputTick(At(t), Sec(0.1));
        }
    }

    // ---- Window switch ----

    [Fact]
    public void Window_switch_captures_after_the_draw_delay()
    {
        var engine = Engine();
        engine.OnWindowChanged(At(0));

        Assert.Equal(At(1), engine.NextDue);
        Assert.Null(engine.Poll(At(0.9)));
        Assert.Equal(new CaptureDecision(At(1), TriggerType.WindowSwitch), engine.Poll(At(1)));
        Assert.Null(engine.Poll(At(1.1)));
    }

    [Fact]
    public void A_second_window_switch_restarts_the_draw_delay()
    {
        var engine = Engine();
        engine.OnWindowChanged(At(0));
        engine.OnWindowChanged(At(0.6));

        Assert.Null(engine.Poll(At(1)));
        Assert.NotNull(engine.Poll(At(1.6)));
    }

    [Fact]
    public void Constant_window_changes_cannot_postpone_a_capture_forever()
    {
        var engine = Engine();
        for (double t = 0; t < 20; t += 0.5)
        {
            engine.OnWindowChanged(At(t));
            if (t < 5)
            {
                Assert.Null(engine.Poll(At(t)));
            }
        }

        // First requested at 0, so it fires no later than 0 + MinCaptureInterval.
        Assert.Equal(At(5), engine.NextDue);
    }

    // ---- Typing pause ----

    [Fact]
    public void Typing_pause_captures_once_after_two_quiet_seconds()
    {
        var engine = Engine();
        engine.OnInputTick(At(0), Sec(0.2));
        Assert.Null(engine.Poll(At(0)));

        engine.OnInputTick(At(1), Sec(1.2));
        Assert.Null(engine.Poll(At(1)));

        engine.OnInputTick(At(2), Sec(2.2));
        Assert.Equal(new CaptureDecision(At(2), TriggerType.TypingPause), engine.Poll(At(2)));

        // Still quiet: no second capture for the same pause.
        engine.OnInputTick(At(3), Sec(3.2));
        engine.OnInputTick(At(10), Sec(10.2));
        Assert.Null(engine.Poll(At(10)));
    }

    [Fact]
    public void No_typing_pause_capture_without_prior_input()
    {
        var engine = Engine();
        engine.OnInputTick(At(0), Sec(30));
        engine.OnInputTick(At(1), Sec(31));

        Assert.Null(engine.Poll(At(1)));
    }

    [Fact]
    public void Continuous_typing_does_not_trigger_a_pause_capture()
    {
        var engine = Engine();
        TypeContinuously(engine, 0, 30);

        Assert.Null(engine.Poll(At(30)));
    }

    // ---- Fallback ----

    [Fact]
    public void Fallback_captures_every_minute_while_active()
    {
        var engine = Engine();
        TypeContinuously(engine, 0, 59);
        Assert.Null(engine.Poll(At(59)));
        Assert.Equal(At(60), engine.NextDue);

        engine.OnInputTick(At(60), Sec(0.1));
        Assert.Equal(new CaptureDecision(At(60), TriggerType.Fallback), engine.Poll(At(60)));

        TypeContinuously(engine, 61, 119);
        Assert.Null(engine.Poll(At(119)));
        Assert.Equal(TriggerType.Fallback, engine.Poll(At(120))?.Trigger);
    }

    [Fact]
    public void Any_capture_restarts_the_fallback_interval()
    {
        var engine = Engine();
        TypeContinuously(engine, 0, 30);
        engine.OnWindowChanged(At(30));
        Assert.NotNull(engine.Poll(At(31)));

        TypeContinuously(engine, 32, 90);
        Assert.Null(engine.Poll(At(90)));
        Assert.Equal(TriggerType.Fallback, engine.Poll(At(91))?.Trigger);
    }

    // ---- Rate limit ----

    [Fact]
    public void Triggers_inside_the_rate_limit_window_coalesce_into_one_capture_at_its_end()
    {
        var engine = Engine();
        engine.OnWindowChanged(At(0));
        Assert.NotNull(engine.Poll(At(1)));

        // Typing pause at 3 and a window switch at 3.5, both inside the window that ends at 6.
        engine.OnInputTick(At(2), Sec(0.1));
        engine.OnInputTick(At(3), Sec(1.1));
        engine.OnInputTick(At(4), Sec(2.1));
        engine.OnWindowChanged(At(4.5));

        Assert.Equal(At(6), engine.NextDue);
        Assert.Null(engine.Poll(At(5.9)));
        // The window switch takes over the label of the coalesced capture.
        Assert.Equal(new CaptureDecision(At(6), TriggerType.WindowSwitch), engine.Poll(At(6)));
        Assert.Null(engine.Poll(At(6.1)));
        Assert.Equal(At(66), engine.NextDue);
    }

    [Fact]
    public void Captures_are_never_closer_than_the_minimum_interval()
    {
        var engine = Engine();
        var captures = new List<DateTimeOffset>();
        for (double t = 0; t <= 60; t += 0.25)
        {
            engine.OnWindowChanged(At(t));
            if (engine.Poll(At(t)) is { } decision)
            {
                captures.Add(decision.Timestamp);
            }
        }

        Assert.True(captures.Count >= 10);
        Assert.All(captures.Zip(captures.Skip(1)), pair => Assert.True(pair.Second - pair.First >= Sec(5)));
    }

    // ---- Idle ----

    [Fact]
    public void Idle_stops_all_captures_and_drops_pending_ones()
    {
        var engine = Engine();
        engine.OnWindowChanged(At(0));
        engine.OnInputTick(At(0.5), Sec(180));

        Assert.True(engine.IsIdle);
        Assert.Null(engine.NextDue);
        Assert.Null(engine.Poll(At(1)));

        // Neither window changes nor the fallback capture while idle.
        engine.OnWindowChanged(At(100));
        engine.OnInputTick(At(101), Sec(280));
        Assert.Null(engine.Poll(At(500)));
    }

    [Fact]
    public void Capturing_resumes_with_the_next_input()
    {
        var engine = Engine();
        engine.OnInputTick(At(0), Sec(0.1));
        engine.OnInputTick(At(200), Sec(200));
        Assert.True(engine.IsIdle);

        engine.OnInputTick(At(300), Sec(0.3));

        Assert.False(engine.IsIdle);
        // Long past the fallback interval, so the first capture comes right away.
        Assert.Equal(TriggerType.Fallback, engine.Poll(At(300))?.Trigger);
    }

    // ---- Locked, asleep, paused ----

    [Fact]
    public void Suspended_stops_all_captures_and_drops_pending_ones()
    {
        var engine = Engine();
        engine.OnWindowChanged(At(0));
        engine.SetSuspended(At(0.5), true);

        Assert.Null(engine.NextDue);
        Assert.Null(engine.Poll(At(1)));

        engine.OnWindowChanged(At(10));
        engine.OnInputTick(At(11), Sec(0.1));
        engine.OnInputTick(At(14), Sec(3.1));
        Assert.Null(engine.Poll(At(500)));
    }

    [Fact]
    public void Leaving_suspended_captures_after_the_draw_delay()
    {
        var engine = Engine();
        engine.OnInputTick(At(0), Sec(0.1));
        engine.SetSuspended(At(10), true);
        engine.SetSuspended(At(600), false);

        Assert.Null(engine.Poll(At(600)));
        Assert.Equal(new CaptureDecision(At(601), TriggerType.WindowSwitch), engine.Poll(At(601)));
    }
}
