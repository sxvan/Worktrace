using Capture.Core.Capturing;
using Capture.Core.Export;
using Capture.Core.Geometry;
using Capture.Core.Monitoring;
using Capture.Core.Redaction;
using Capture.Core.Triggers;
using Microsoft.Data.Sqlite;

namespace Capture.Storage.Tests;

/// <summary>Writes through CaptureDatabase, reads back through CaptureReader, as the tray app and the CLI do.</summary>
public sealed class ExportRoundTripTests : IDisposable
{
    // Fake secrets are assembled at run time so no scanner mistakes this file for a leak.
    private static readonly string Token = "gh" + "p_" + string.Concat(Enumerable.Repeat("a1B2c3D4e5", 4));

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "timecapture-tests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "capture.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WindowPlacement Placement(long handle, string process, string title)
    {
        var bounds = new RectInt(0, 0, 1000, 800);
        var window = new WindowInfo(handle, bounds, bounds, 0, true, true, process, title);
        return new WindowPlacement(window, [bounds], new MonitorInfo(1, 1, "m", bounds, 1.0, true));
    }

    [Fact]
    public void Titles_are_scrubbed_on_the_way_in()
    {
        using (var db = new CaptureDatabase(DbPath, TextScrubber.Default))
        {
            db.InsertEvent(new ActivityEvent(T0, EventType.Foreground, "WindowsTerminal.exe", $"export TOKEN={Token}", 1));
        }

        using var reader = new CaptureReader(DbPath);
        TimelineEvent e = Assert.Single(reader.ReadEvents(T0, T0.AddHours(1)));
        Assert.DoesNotContain(Token, e.WindowTitle);
        Assert.Contains(TextScrubber.RedactedMarker, e.WindowTitle);
    }

    [Fact]
    public void Text_of_a_duplicate_window_comes_from_the_original()
    {
        using (var db = new CaptureDatabase(DbPath, TextScrubber.Default))
        {
            db.InsertEvent(new ActivityEvent(T0, EventType.Foreground, "Code.exe", "Redaction.cs - open-timesheet", 1));
            ScreenTextLine[] text = [new("public interface IRedactor", 0.3, 0.2, 0.6, 0.23)];
            long original = db.InsertCapture(T0.AddSeconds(5), TriggerType.WindowSwitch, 1,
                [new CapturedWindowRecord(Placement(1, "Code.exe", "Redaction.cs - open-timesheet"), true, 1000, 800, 42, "captures\\2026-11-20\\a.avif", null, 0, text)])[0];
            db.InsertCapture(T0.AddSeconds(65), TriggerType.Fallback, 1,
                [new CapturedWindowRecord(Placement(1, "Code.exe", "Redaction.cs - open-timesheet"), true, 1000, 800, 42, null, original)]);
            db.InsertEvent(new ActivityEvent(T0.AddMinutes(10), EventType.IdleStart));
        }

        using var reader = new CaptureReader(DbPath);
        DateTimeOffset from = T0, to = T0.AddHours(1);
        IReadOnlyList<TextSample> samples = reader.ReadFocusedText(from, to);
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal("public interface IRedactor", Assert.Single(s.Lines).Text));

        IReadOnlyList<Visit> visits = VisitBuilder.Build(reader.ReadEvents(from, to), from, to, reader.LastRecordBefore(to)!.Value.AddMinutes(2));
        IReadOnlyList<ExportVisit> export = ExportFormatter.Assemble(
            visits, samples, reader.ReadFocusedImages(from, to), new ExportOptions(), TextScrubber.Default);

        ExportVisit code = export[0];
        Assert.Equal(VisitKind.Window, code.Visit.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), code.Visit.Duration);
        ExportTextLine line = Assert.Single(code.Text);
        Assert.Equal(("main", 2), (line.Region, line.Count));
        Assert.Single(code.ImageIds);

        // Idle until two minutes after the last record, then nothing.
        Assert.Equal(VisitKind.Idle, export[1].Visit.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), export[1].Visit.Duration);

        string output = ExportFormatter.ToText(export, from, to, new ExportOptions(), TimeZoneInfo.Utc);
        Assert.Contains("08:00:00-08:10:00  10m00s  Code.exe \"Redaction.cs - open-timesheet\"", output);
        Assert.Contains("    [main] public interface IRedactor (x2)", output);
    }

    [Fact]
    public void Text_retention_removes_old_text_only()
    {
        using var db = new CaptureDatabase(DbPath, TextScrubber.Default);
        ScreenTextLine[] text = [new("hello world", 0.3, 0.2, 0.6, 0.23)];
        db.InsertCapture(T0, TriggerType.Fallback, 1,
            [new CapturedWindowRecord(Placement(1, "Code.exe", "t"), true, 1000, 800, 1, null, null, 0, text)]);
        db.InsertCapture(T0.AddDays(2), TriggerType.Fallback, 1,
            [new CapturedWindowRecord(Placement(1, "Code.exe", "t"), true, 1000, 800, 2, null, null, 0, text)]);

        Assert.Equal(1, db.DeleteTextBefore(T0.AddDays(1)));

        using var reader = new CaptureReader(DbPath);
        Assert.Empty(reader.ReadFocusedText(T0, T0.AddDays(1)));
        Assert.Single(reader.ReadFocusedText(T0.AddDays(1), T0.AddDays(3)));
    }
}
