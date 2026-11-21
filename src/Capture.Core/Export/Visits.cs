using Capture.Core.Monitoring;

namespace Capture.Core.Export;

/// <summary>One row of the events table, as read back for an export.</summary>
public sealed record TimelineEvent(DateTimeOffset Timestamp, EventType Type, string? ProcessName = null, string? WindowTitle = null);

public enum VisitKind
{
    /// <summary>A window with a rule had focus.</summary>
    Window,
    /// <summary>A window without a rule had focus: no name or title is known.</summary>
    Other,
    Idle,
    Locked,
    Asleep,
    Paused,
}

/// <summary>A stretch of time with one state: one window in front, or one kind of break.</summary>
public sealed record Visit(DateTimeOffset Start, DateTimeOffset End, VisitKind Kind, string? ProcessName = null, string? WindowTitle = null)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>A captured window whose screenshot can be requested, for pointing at it from the export.</summary>
/// <param name="WindowId">capture_windows.id, which the screenshot command takes.</param>
public sealed record ScreenshotRef(DateTimeOffset Timestamp, long WindowId, string ProcessName);

/// <summary>
/// Turns the event log into visits. Each event lasts until the next one. Breaks (sleep, lock,
/// pause, idle) cover the window underneath until they end; consecutive equal states are merged.
/// </summary>
public static class VisitBuilder
{
    /// <param name="events">
    /// All events of the range in any order, plus the last event before <paramref name="from"/>
    /// so the state at the start is known.
    /// </param>
    /// <param name="dataEnd">
    /// After this time nothing was recorded (the app was not running), so visits stop there.
    /// Null if recording is still going on.
    /// </param>
    public static IReadOnlyList<Visit> Build(
        IEnumerable<TimelineEvent> events, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? dataEnd = null)
    {
        DateTimeOffset end = dataEnd is { } d && d < to ? d : to;
        var visits = new List<Visit>();
        var state = new State();
        DateTimeOffset? since = null;

        // Stable: events at the same time keep the order in which they were written.
        foreach (TimelineEvent e in events.Select((e, i) => (e, i)).OrderBy(x => x.e.Timestamp).ThenBy(x => x.i).Select(x => x.e))
        {
            if (since is { } start)
            {
                Emit(visits, state, Max(start, from), Min(e.Timestamp, end));
            }
            state.Apply(e);
            since = e.Timestamp;
        }

        if (since is { } last)
        {
            Emit(visits, state, Max(last, from), end);
        }
        return visits;
    }

    /// <summary>
    /// Removes window visits shorter than <paramref name="minimum"/> (passing through windows
    /// while alt-tabbing, a dialog that flashes up): their time goes to the visit before, or to the
    /// one after if they come first, and neighbours that are then equal are merged. Breaks (idle,
    /// locked, asleep, paused) are always kept, however short.
    /// </summary>
    public static IReadOnlyList<Visit> DropShortSwitches(IReadOnlyList<Visit> visits, TimeSpan minimum)
    {
        var kept = new List<Visit>(visits.Count);
        DateTimeOffset? pendingStart = null;
        foreach (Visit visit in visits)
        {
            bool isWindow = visit.Kind is VisitKind.Window or VisitKind.Other;
            if (isWindow && visit.Duration < minimum)
            {
                if (kept.Count > 0 && kept[^1].End == visit.Start)
                {
                    kept[^1] = kept[^1] with { End = visit.End };
                }
                else
                {
                    pendingStart ??= visit.Start;
                }
                continue;
            }

            Visit current = pendingStart is { } start && start < visit.Start ? visit with { Start = start } : visit;
            pendingStart = null;

            if (kept.Count > 0 && kept[^1] is var previous && previous.End == current.Start && previous.Kind == current.Kind
                && previous.ProcessName == current.ProcessName && previous.WindowTitle == current.WindowTitle)
            {
                kept[^1] = previous with { End = current.End };
            }
            else
            {
                kept.Add(current);
            }
        }
        return kept;
    }

    private static void Emit(List<Visit> visits, State state, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start || state.Current() is not { } current)
        {
            return;
        }

        if (visits.Count > 0 && visits[^1] is var previous && previous.End == start && previous.Kind == current.Kind
            && previous.ProcessName == current.ProcessName && previous.WindowTitle == current.WindowTitle)
        {
            visits[^1] = previous with { End = end };
            return;
        }
        visits.Add(new Visit(start, end, current.Kind, current.ProcessName, current.WindowTitle));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed class State
    {
        private (VisitKind Kind, string? ProcessName, string? WindowTitle)? _window;
        private bool _idle, _locked, _asleep, _paused;

        public void Apply(TimelineEvent e)
        {
            switch (e.Type)
            {
                case EventType.Foreground or EventType.TitleChange:
                    _window = (VisitKind.Window, e.ProcessName, e.WindowTitle);
                    break;
                case EventType.Other:
                    _window = (VisitKind.Other, null, null);
                    break;
                case EventType.IdleStart: _idle = true; break;
                case EventType.IdleEnd: _idle = false; break;
                case EventType.Lock: _locked = true; break;
                case EventType.Unlock: _locked = false; break;
                case EventType.Sleep: _asleep = true; break;
                case EventType.Resume:
                    _asleep = false;
                    _idle = false;
                    break;
                case EventType.Paused: _paused = true; break;
                case EventType.Resumed: _paused = false; break;
            }
        }

        /// <summary>The state to show, or null if no window has been seen yet.</summary>
        public (VisitKind Kind, string? ProcessName, string? WindowTitle)? Current() =>
            _asleep ? (VisitKind.Asleep, null, null)
            : _locked ? (VisitKind.Locked, null, null)
            : _paused ? (VisitKind.Paused, null, null)
            : _idle ? (VisitKind.Idle, null, null)
            : _window;
    }
}
