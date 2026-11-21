using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Capture.Core.Redaction;

namespace Capture.Core.Export;

/// <summary>What an export contains. Each layer includes the ones below it.</summary>
public enum ExportLayer
{
    /// <summary>Visits with application name and window title.</summary>
    Titles = 1,
    /// <summary>Plus the recognized text of the captured windows, with positions.</summary>
    Text = 2,
}

public sealed record ExportOptions(
    ExportLayer Layer = ExportLayer.Text,
    int MaxLinesPerVisit = 12,
    int MaxLineLength = 160,
    int MaxImagesPerVisit = 3);

/// <summary>One visit of an export, with the text and images attached to it.</summary>
public sealed record ExportVisit(Visit Visit, IReadOnlyList<ExportTextLine> Text, IReadOnlyList<long> ImageIds);

/// <param name="Region">Where in the window: top, left, main, right or bottom.</param>
/// <param name="Count">In how many captures of the visit the line appeared.</param>
public sealed record ExportTextLine(string Region, string Text, int Count);

/// <summary>
/// Builds the export of a time range: the visits, and for layer 2 the most telling lines of
/// text per visit. Everything that comes out is scrubbed once more, so rows stored before
/// scrubbing existed cannot slip through.
/// </summary>
public static class ExportFormatter
{
    private static readonly string[] RegionOrder = ["main", "top", "left", "right", "bottom"];

    public static IReadOnlyList<ExportVisit> Assemble(
        IReadOnlyList<Visit> visits, IReadOnlyList<TextSample> text, IReadOnlyList<ImageRef> images,
        ExportOptions options, TextScrubber scrubber)
    {
        var result = new List<ExportVisit>(visits.Count);
        foreach (Visit visit in visits)
        {
            Visit scrubbed = visit with { WindowTitle = scrubber.Scrub(visit.WindowTitle) };
            if (visit.Kind != VisitKind.Window)
            {
                result.Add(new ExportVisit(scrubbed, [], []));
                continue;
            }

            bool Inside(DateTimeOffset t, string process) =>
                t >= visit.Start && t < visit.End && string.Equals(process, visit.ProcessName, StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<ExportTextLine> lines = options.Layer >= ExportLayer.Text
                ? PickLines(text.Where(s => Inside(s.Timestamp, s.ProcessName)), options, scrubber)
                : [];
            List<long> imageIds = images.Where(i => Inside(i.Timestamp, i.ProcessName)).Select(i => i.WindowId).ToList();

            result.Add(new ExportVisit(scrubbed, lines, Sample(imageIds, options.MaxImagesPerVisit)));
        }
        return result;
    }

    public static string ToText(
        IReadOnlyList<ExportVisit> visits, DateTimeOffset from, DateTimeOffset to, ExportOptions options, TimeZoneInfo zone)
    {
        var builder = new StringBuilder();
        DateTimeOffset localFrom = TimeZoneInfo.ConvertTime(from, zone);
        DateTimeOffset localTo = TimeZoneInfo.ConvertTime(to, zone);
        builder.AppendLine("# TimeCapture export");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"range: {localFrom:yyyy-MM-dd HH:mm} to {localTo:yyyy-MM-dd HH:mm} (local time, UTC{localFrom:zzz})");
        builder.AppendLine(options.Layer >= ExportLayer.Text
            ? "layer: 2 (window titles and recognized text; secrets replaced by [redacted])"
            : "layer: 1 (window titles only; secrets replaced by [redacted])");
        builder.AppendLine("format: start-end duration process \"title\"; indented lines are text seen in the window as [region] text (xN = seen in N captures); images: ids for the image command");
        builder.AppendLine();

        foreach (ExportVisit item in visits)
        {
            Visit v = item.Visit;
            builder.Append(CultureInfo.InvariantCulture,
                $"{TimeZoneInfo.ConvertTime(v.Start, zone):HH:mm:ss}-{TimeZoneInfo.ConvertTime(v.End, zone):HH:mm:ss} {FormatDuration(v.Duration),7}  ");
            builder.AppendLine(v.Kind switch
            {
                VisitKind.Window => $"{v.ProcessName} \"{v.WindowTitle}\"",
                VisitKind.Other => "[other application]",
                VisitKind.Idle => "[idle]",
                VisitKind.Locked => "[locked]",
                VisitKind.Asleep => "[asleep]",
                VisitKind.Paused => "[recording paused]",
                _ => $"[{v.Kind}]",
            });

            foreach (ExportTextLine line in item.Text)
            {
                builder.Append(CultureInfo.InvariantCulture, $"    [{line.Region}] {line.Text}");
                builder.AppendLine(line.Count > 1 ? $" (x{line.Count})" : "");
            }
            if (item.ImageIds.Count > 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"    images: {string.Join(", ", item.ImageIds)}");
            }
        }
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(IReadOnlyList<ExportVisit> visits, DateTimeOffset from, DateTimeOffset to, ExportOptions options, TimeZoneInfo zone)
    {
        var document = new
        {
            from = TimeZoneInfo.ConvertTime(from, zone),
            to = TimeZoneInfo.ConvertTime(to, zone),
            layer = (int)options.Layer,
            visits = visits.Select(item => new
            {
                start = TimeZoneInfo.ConvertTime(item.Visit.Start, zone),
                end = TimeZoneInfo.ConvertTime(item.Visit.End, zone),
                seconds = (int)item.Visit.Duration.TotalSeconds,
                kind = item.Visit.Kind.ToString().ToLowerInvariant(),
                process = item.Visit.ProcessName,
                title = item.Visit.WindowTitle,
                text = item.Text.Count == 0 ? null : item.Text,
                images = item.ImageIds.Count == 0 ? null : item.ImageIds,
            }),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        int seconds = (int)Math.Round(duration.TotalSeconds);
        return seconds switch
        {
            < 60 => $"{seconds}s",
            < 3600 => $"{seconds / 60}m{seconds % 60:00}s",
            _ => $"{seconds / 3600}h{seconds % 3600 / 60:00}m",
        };
    }

    /// <summary>Where a line sits in the window, judged by the centre of its box.</summary>
    public static string RegionOf(ScreenTextLine line)
    {
        double x = (line.Left + line.Right) / 2, y = (line.Top + line.Bottom) / 2;
        return y < 0.07 ? "top"
            : y > 0.93 ? "bottom"
            : x < 0.25 ? "left"
            : x > 0.75 ? "right"
            : "main";
    }

    /// <summary>
    /// The lines worth showing for a visit: those that appear often and in large type, main area
    /// first. Short fragments and bare markers say nothing and are left out.
    /// </summary>
    private static IReadOnlyList<ExportTextLine> PickLines(IEnumerable<TextSample> samples, ExportOptions options, TextScrubber scrubber)
    {
        var seen = new Dictionary<(string Region, string Text), (int Count, double Height, double Top, int Order)>();
        foreach (TextSample sample in samples.OrderBy(s => s.Timestamp))
        {
            // Each capture counts a line once, however often it shows.
            var inThisCapture = new HashSet<(string, string)>();
            foreach (ScreenTextLine line in sample.Lines)
            {
                string text = scrubber.Scrub(line.Text) ?? "";
                if (text.Length > options.MaxLineLength)
                {
                    text = text[..options.MaxLineLength] + "...";
                }
                if (!IsTelling(text))
                {
                    continue;
                }

                var key = (RegionOf(line), text);
                if (!inThisCapture.Add(key))
                {
                    continue;
                }
                seen[key] = seen.TryGetValue(key, out var known)
                    ? (known.Count + 1, Math.Max(known.Height, line.Height), known.Top, known.Order)
                    : (1, line.Height, line.Top, seen.Count);
            }
        }

        // Larger text counts more: a heading in 20 px type is worth more than a 10 px sidebar entry.
        return seen
            .OrderByDescending(kv => kv.Value.Count * (1 + kv.Value.Height * 40))
            .ThenBy(kv => kv.Value.Order)
            .Take(options.MaxLinesPerVisit)
            .OrderBy(kv => Array.IndexOf(RegionOrder, kv.Key.Region))
            .ThenBy(kv => kv.Value.Top)
            .Select(kv => new ExportTextLine(kv.Key.Region, kv.Key.Text, kv.Value.Count))
            .ToList();
    }

    private static bool IsTelling(string text)
    {
        string withoutMarkers = text.Replace(TextScrubber.RedactedMarker, "", StringComparison.Ordinal);
        return withoutMarkers.Count(char.IsLetterOrDigit) >= 3;
    }

    /// <summary>First, last and evenly spread ones in between.</summary>
    private static IReadOnlyList<long> Sample(List<long> ids, int max)
    {
        if (ids.Count <= max || max <= 0)
        {
            return max <= 0 ? [] : ids;
        }
        if (max == 1)
        {
            return [ids[ids.Count / 2]];
        }
        return Enumerable.Range(0, max).Select(i => ids[(int)Math.Round(i * (ids.Count - 1) / (double)(max - 1))]).Distinct().ToList();
    }
}
