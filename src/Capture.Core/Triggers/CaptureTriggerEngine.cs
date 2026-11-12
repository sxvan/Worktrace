using Capture.Core.Config;

namespace Capture.Core.Triggers;

public enum TriggerType
{
    WindowSwitch,
    TypingPause,
    Fallback,
}

public static class TriggerTypeNames
{
    public static string ToDbName(this TriggerType type) => type switch
    {
        TriggerType.WindowSwitch => "window_switch",
        TriggerType.TypingPause => "typing_pause",
        TriggerType.Fallback => "fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

public sealed record TriggerSettings(
    TimeSpan WindowSwitchDelay,
    TimeSpan TypingPause,
    TimeSpan FallbackInterval,
    TimeSpan MinCaptureInterval,
    TimeSpan IdleTimeout)
{
    public static TriggerSettings FromConfig(CaptureConfig config) => new(
        TimeSpan.FromMilliseconds(config.WindowSwitchDelayMs),
        TimeSpan.FromMilliseconds(config.TypingPauseMs),
        TimeSpan.FromSeconds(config.FallbackIntervalSeconds),
        TimeSpan.FromSeconds(config.MinCaptureIntervalSeconds),
        TimeSpan.FromSeconds(config.IdleTimeoutSeconds));
}

/// <summary>A capture should be taken now. <see cref="Trigger"/> is the first trigger that asked for it.</summary>
public sealed record CaptureDecision(DateTimeOffset Timestamp, TriggerType Trigger);

/// <summary>
/// Decides when to capture. Pure logic with the clock passed in; not thread-safe.
///
/// Rules:
///  - Window switch: capture <see cref="TriggerSettings.WindowSwitchDelay"/> after the foreground window or its title changed.
///  - Typing pause: capture once input is followed by <see cref="TriggerSettings.TypingPause"/> without input.
///  - Fallback: capture when the user is active and the last capture is <see cref="TriggerSettings.FallbackInterval"/> old.
///  - Rate limit: captures are at least <see cref="TriggerSettings.MinCaptureInterval"/> apart; triggers in between
///    are coalesced into one capture at the end of that window.
///  - Idle (no input for <see cref="TriggerSettings.IdleTimeout"/>) and suspended (locked, asleep, paused): no captures.
/// </summary>
public sealed class CaptureTriggerEngine(TriggerSettings settings)
{
    private sealed record Pending(TriggerType Trigger, DateTimeOffset Due, DateTimeOffset Latest);

    private Pending? _pending;
    private DateTimeOffset? _lastCapture;
    private DateTimeOffset? _fallbackFrom;
    private DateTimeOffset? _lastTick;
    private bool _inputSinceLastPause;
    private bool _idle;
    private bool _suspended;

    public bool IsIdle => _idle;
    public bool IsSuspended => _suspended;
    private bool CanCapture => !_idle && !_suspended;

    /// <summary>When <see cref="Poll"/> should next be called, or null if nothing is scheduled.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            if (!CanCapture)
            {
                return null;
            }
            if (_pending is not null)
            {
                return _pending.Due;
            }
            return _fallbackFrom + settings.FallbackInterval;
        }
    }

    /// <summary>The foreground window or its title changed.</summary>
    public void OnWindowChanged(DateTimeOffset now)
    {
        _fallbackFrom ??= now;
        Request(now, TriggerType.WindowSwitch, settings.WindowSwitchDelay);
    }

    /// <summary>Periodic input poll; <paramref name="idleFor"/> is the time since the last input.</summary>
    public void OnInputTick(DateTimeOffset now, TimeSpan idleFor)
    {
        _fallbackFrom ??= now;

        // Input happened since the previous tick if the idle time is shorter than the gap between ticks.
        TimeSpan sinceLastTick = _lastTick is { } last ? now - last : settings.TypingPause;
        bool inputSinceLastTick = idleFor < sinceLastTick;
        _lastTick = now;

        if (idleFor >= settings.IdleTimeout)
        {
            _idle = true;
            _pending = null;
            _inputSinceLastPause = false;
            return;
        }
        _idle = false;

        if (inputSinceLastTick)
        {
            _inputSinceLastPause = true;
        }
        if (_inputSinceLastPause && idleFor >= settings.TypingPause)
        {
            _inputSinceLastPause = false;
            Request(now, TriggerType.TypingPause, TimeSpan.Zero);
        }
    }

    /// <summary>Locked, asleep or paused. Leaving that state schedules a capture like a window switch.</summary>
    public void SetSuspended(DateTimeOffset now, bool suspended)
    {
        if (suspended == _suspended)
        {
            return;
        }
        _suspended = suspended;
        _pending = null;
        _inputSinceLastPause = false;

        if (!suspended)
        {
            // The desktop is still being redrawn right after unlock or resume.
            _fallbackFrom = now;
            Request(now, TriggerType.WindowSwitch, settings.WindowSwitchDelay);
        }
    }

    /// <summary>Returns a decision if a capture is due at <paramref name="now"/>, and starts the rate-limit window.</summary>
    public CaptureDecision? Poll(DateTimeOffset now)
    {
        if (!CanCapture)
        {
            return null;
        }

        TriggerType trigger;
        if (_pending is not null)
        {
            if (now < _pending.Due)
            {
                return null;
            }
            trigger = _pending.Trigger;
        }
        else if (_fallbackFrom is { } from && now >= from + settings.FallbackInterval)
        {
            trigger = TriggerType.Fallback;
        }
        else
        {
            return null;
        }

        _pending = null;
        _lastCapture = now;
        _fallbackFrom = now;
        return new CaptureDecision(now, trigger);
    }

    private void Request(DateTimeOffset now, TriggerType trigger, TimeSpan delay)
    {
        if (!CanCapture)
        {
            return;
        }

        DateTimeOffset earliest = _lastCapture is { } last ? last + settings.MinCaptureInterval : DateTimeOffset.MinValue;
        DateTimeOffset due = Max(now + delay, earliest);

        if (_pending is null)
        {
            // A stream of window changes may push the capture back, but never further than this.
            _pending = new Pending(trigger, due, Latest: Max(now + settings.MinCaptureInterval, due));
        }
        else if (trigger == TriggerType.WindowSwitch)
        {
            // Give the newest window time to draw.
            _pending = _pending with { Due = Min(Max(_pending.Due, due), _pending.Latest) };
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
