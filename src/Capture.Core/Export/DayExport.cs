using System.Globalization;
using System.Text;

namespace Capture.Core.Export;

/// <summary>
/// The daily export a model reads first: visits with window titles (already scrubbed when they were
/// stored), which screenshots exist for each, and the Claude Code sessions of the day. It never
/// contains screen content: screenshots are only pointed to by id, and each one has to be
/// requested and then approved by the user.
/// </summary>
public static class DayExport
{
    /// <summary>At most this many screenshot ids are listed per visit, spread over its length.</summary>
    public const int MaxScreenshotsPerVisit = 6;

    /// <summary>At most this many prompts are listed per session.</summary>
    public const int MaxPromptsPerSession = 40;

    public static string ToText(
        DateOnly day,
        IReadOnlyList<Visit> visits,
        IReadOnlyList<ScreenshotRef> screenshots,
        IReadOnlyList<AgentSession> sessions,
        TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        string Time(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string Minute(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

        TimeSpan offset = zone.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0)));
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        text.Append(CultureInfo.InvariantCulture,
            $"# TimeCapture export {day:yyyy-MM-dd} ({day.DayOfWeek}), local time (UTC{sign}{offset:hh\\:mm})\n\n");

        text.Append("## Visits\n");
        text.Append("Focused window or break; each lasts until the next. Titles are scrubbed of secrets.\n");
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
                VisitKind.Other => "(application without a rule)",
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
                    text.Append(CultureInfo.InvariantCulture, $" {shot.WindowId}@{Minute(shot.Timestamp)}");
                }
                if (inVisit.Count > MaxScreenshotsPerVisit)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({inVisit.Count} in all)");
                }
            }
            text.Append('\n');
        }

        text.Append("\n## Claude Code sessions\n");
        text.Append("Active spans (pauses over 10 minutes split them), folder, branch, and the prompts typed.\n\n");
        if (sessions.Count == 0)
        {
            text.Append("(none)\n");
        }
        foreach (AgentSession session in sessions.OrderBy(s => s.ActiveSpans[0].Start))
        {
            text.Append("### ").Append(session.WorkingDirectory ?? "(unknown folder)");
            if (session.GitBranches.Count > 0)
            {
                text.Append(" [").AppendJoin(", ", session.GitBranches).Append(']');
            }
            text.Append('\n');
            text.Append("active: ").AppendJoin(", ", session.ActiveSpans.Select(s => $"{Minute(s.Start)}-{Minute(s.End)}")).Append('\n');
            foreach (AgentPrompt prompt in Spread(session.Prompts, MaxPromptsPerSession))
            {
                text.Append(CultureInfo.InvariantCulture, $"- {Minute(prompt.Timestamp)} {prompt.Text}\n");
            }
            if (session.Prompts.Count > MaxPromptsPerSession)
            {
                text.Append(CultureInfo.InvariantCulture, $"- ({session.Prompts.Count - MaxPromptsPerSession} more prompts not shown)\n");
            }
            text.Append('\n');
        }

        return text.ToString();
    }

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
