namespace Capture.Core.Monitoring;

public enum EventType
{
    Foreground,
    TitleChange,
    IdleStart,
    IdleEnd,
    Lock,
    Unlock,
    Sleep,
    Resume,
    /// <summary>A window without a rule is in front, or no capturable window is visible.</summary>
    Other,
    Paused,
    Resumed,
}

/// <summary>One row of the events table. Process, title and handle are only set as far as the window's rule allows.</summary>
public sealed record ActivityEvent(
    DateTimeOffset Timestamp,
    EventType Type,
    string? ProcessName = null,
    string? WindowTitle = null,
    long? WindowHandle = null);

public static class EventTypeNames
{
    public static string ToDbName(this EventType type) => type switch
    {
        EventType.Foreground => "foreground",
        EventType.TitleChange => "title_change",
        EventType.IdleStart => "idle_start",
        EventType.IdleEnd => "idle_end",
        EventType.Lock => "lock",
        EventType.Unlock => "unlock",
        EventType.Sleep => "sleep",
        EventType.Resume => "resume",
        EventType.Other => "other",
        EventType.Paused => "paused",
        EventType.Resumed => "resumed",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static EventType FromDbName(string name) =>
        Enum.GetValues<EventType>().First(t => t.ToDbName() == name);
}
