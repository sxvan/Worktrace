using System.Globalization;
using System.Text;

namespace Capture.Core.Export;

/// <summary>
/// The daily export a model reads first: what TimeCapture recorded, that is, visits with window
/// titles (already scrubbed when they were stored) and which screenshots exist for each. It never
/// contains screen content: screenshots are only pointed to by id, and each one has to be
/// requested and then approved by the user. Claude Code sessions are a separate export
/// (<see cref="SessionsExport"/>).
/// </summary>
public static class DayExport
{
    /// <summary>At most this many screenshot ids are listed per visit, spread over its length.</summary>
    public const int MaxScreenshotsPerVisit = 6;

    /// <summary>Window visits shorter than this are folded into their neighbours (see <see cref="VisitBuilder.DropShortSwitches"/>).</summary>
    public static readonly TimeSpan MinimumSwitch = TimeSpan.FromSeconds(3);

    public static string ToText(DateOnly day, IReadOnlyList<Visit> visits, IReadOnlyList<ScreenshotRef> screenshots, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        string Time(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        text.Append(Heading("TimeCapture export", day, zone));
        text.Append("## Visits\n");
        text.Append("Focused window or break; each lasts until the next. Titles are scrubbed of secrets.\n");
        text.Append("Window switches under 3 s (alt-tab, flashing dialogs) are counted to the visit before.\n");
        text.Append("`shots` are screenshot ids of the focused window during the visit (`screenshot <id>`).\n\n");
        if (visits.Count == 0)
        {
            text.Append("(nothing recorded)\n");
        }

        int first = 0;
        foreach (Visit visit in visits)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Time(visit.Start)}-{Time(visit.End)} {FormatDuration(visit.Duration),7}  ");
            text.Append(visit.Kind switch
            {
                VisitKind.Window => $"{visit.ProcessName} \"{visit.WindowTitle}\"",
                VisitKind.Other => "(window not recorded: no rule matches it, e.g. no title, or nothing capturable on screen)",
                VisitKind.Idle => "(idle)",
                VisitKind.Locked => "(locked)",
                VisitKind.Asleep => "(asleep)",
                VisitKind.Paused => "(recording paused)",
                _ => visit.Kind.ToString(),
            });

            // Screenshots and visits are both ordered by time.
            while (first < screenshots.Count && screenshots[first].Timestamp < visit.Start)
            {
                first++;
            }
            var inVisit = new List<ScreenshotRef>();
            for (int i = first; i < screenshots.Count && screenshots[i].Timestamp < visit.End; i++)
            {
                if (visit.Kind == VisitKind.Window
                    && string.Equals(screenshots[i].ProcessName, visit.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    inVisit.Add(screenshots[i]);
                }
            }
            if (inVisit.Count > 0)
            {
                text.Append("  shots:");
                foreach (ScreenshotRef shot in Spread(inVisit, MaxScreenshotsPerVisit))
                {
                    text.Append(CultureInfo.InvariantCulture, $" {shot.WindowId}@{Minute(shot.Timestamp, zone)}");
                }
                if (inVisit.Count > MaxScreenshotsPerVisit)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({inVisit.Count} in all)");
                }
            }
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>"# title yyyy-MM-dd (Weekday), local time (UTC+hh:mm)" and a blank line.</summary>
    internal static string Heading(string title, DateOnly day, TimeZoneInfo zone)
    {
        TimeSpan offset = zone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0)));
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture,
            $"# {title} {day:yyyy-MM-dd} ({day.DayOfWeek}), local time (UTC{sign}{offset:hh\\:mm})\n\n");
    }

    internal static string Minute(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>At most <paramref name="max"/> items, evenly spread, always with the first and the last.</summary>
    internal static IEnumerable<T> Spread<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max)
        {
            return items;
        }
        return Enumerable.Range(0, max).Select(i => items[(int)Math.Round(i * (items.Count - 1) / (double)(max - 1))]);
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h{duration.Minutes:00}m")
        : duration.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalSeconds}s");
}
