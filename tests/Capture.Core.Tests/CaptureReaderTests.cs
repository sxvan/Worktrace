using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Monitoring;
using Capture.Core.Redaction;
using Capture.Core.Triggers;
using Capture.Storage;
using Microsoft.Data.Sqlite;

namespace Capture.Core.Tests;

public sealed class CaptureReaderTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ots-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly CaptureDatabase _database;
    private readonly XorProtector _protector = new();

    public CaptureReaderTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "capture.db");
        _database = new CaptureDatabase(_dbPath, TextScrubber.Default);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] key) => [.. key.Select(b => (byte)(b ^ 0x5A)), 0xEE];
        public byte[] Unprotect(byte[] wrapped) => wrapped[..^1].Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private static WindowPlacement Placement(long handle, string process, string title)
    {
        var bounds = new RectInt(0, 0, 100, 100);
        return new WindowPlacement(new WindowInfo(handle, bounds, bounds, 0, true, false, process, title), [bounds]);
    }

    /// <summary>Stores one capture with a focused window; with a file, writes an encrypted image for it.</summary>
    private long Capture(int minute, string? fileName, long? dedupeOf = null, bool focused = true, string title = "Program.cs")
    {
        string? path = null;
        if (fileName is not null)
        {
            path = Path.Combine("captures", Day.ToString("yyyy-MM-dd"), fileName);
            new FileImageStore(_root, new DayKeyStore(_database, _protector)).Write(path, Day, (byte[])Png.Clone());
        }
        return _database.InsertCapture(T0.AddMinutes(minute), TriggerType.Fallback,
            [new CapturedWindowRecord(Placement(7, "Code.exe", title), focused, path, dedupeOf)])[0];
    }

    [Fact]
    public void Deleting_recent_captures_keeps_older_ones_and_events()
    {
        _database.InsertEvent(new ActivityEvent(T0.AddMinutes(9), EventType.Foreground, "Code.exe", "Program.cs"));
        long kept = Capture(1, "a.png.enc");
        long recent = Capture(10, "b.png.enc");
        Capture(11, null, dedupeOf: recent);
        Capture(12, null, dedupeOf: kept);

        IReadOnlyList<string> files = _database.DeleteCapturesSince(T0.AddMinutes(10));

        Assert.Equal([Path.Combine("captures", Day.ToString("yyyy-MM-dd"), "b.png.enc")], files);
        using var reader = new CaptureReader(_dbPath);
        Assert.Equal([kept], reader.ReadFocusedScreenshots(T0, T0.AddHours(1)).Select(s => s.WindowId));
        Assert.Single(reader.ReadEvents(T0, T0.AddHours(1)));
    }

    [Fact]
    public void Deleting_recent_events_keeps_older_ones()
    {
        _database.InsertEvent(new ActivityEvent(T0, EventType.Foreground, "Code.exe", "Program.cs"));
        _database.InsertEvent(new ActivityEvent(T0.AddMinutes(10), EventType.IdleStart));

        Assert.Equal(1, _database.DeleteEventsSince(T0.AddMinutes(10)));

        using var reader = new CaptureReader(_dbPath);
        Assert.Equal([EventType.Foreground], reader.ReadEvents(T0, T0.AddHours(1)).Select(e => e.Type));
    }

    [Fact]
    public void Events_and_screenshots_are_read_back()
    {
        _database.InsertEvent(new ActivityEvent(T0, EventType.Foreground, "Code.exe", "Program.cs"));
        _database.InsertEvent(new ActivityEvent(T0.AddMinutes(5), EventType.IdleStart));
        long first = Capture(1, "a.png.enc");
        Capture(2, null, dedupeOf: first);
        Capture(3, "b.png.enc", focused: false);

        using var reader = new CaptureReader(_dbPath);

        Assert.Equal([EventType.Foreground, EventType.IdleStart], reader.ReadEvents(T0, T0.AddHours(1)).Select(e => e.Type));
        // Only focused windows with an image of their own; the duplicate shows the same.
        Assert.Equal([first], reader.ReadFocusedScreenshots(T0, T0.AddHours(1)).Select(s => s.WindowId));
        Assert.Equal(T0.AddMinutes(5), reader.LastRecordBefore(T0.AddHours(1)));
    }

    [Fact]
    public void A_duplicate_leads_to_the_image_it_refers_to_and_decrypts()
    {
        long first = Capture(1, "a.png.enc");
        long duplicate = Capture(2, null, dedupeOf: first);

        using var reader = new CaptureReader(_dbPath);
        StoredScreenshot shot = reader.FindScreenshot(duplicate)!;

        Assert.Equal(duplicate, shot.WindowId);
        Assert.EndsWith("a.png.enc", shot.RelativePath);
        // The image is the one taken a minute earlier, and says so.
        Assert.True(shot.IsReused);
        Assert.Equal(T0.AddMinutes(1), shot.ImageTimestamp);
        Assert.Equal(T0.AddMinutes(2), shot.Timestamp);
        Assert.False(reader.FindScreenshot(first)!.IsReused);
        Assert.Equal(Png, reader.ReadUnredactedImage(_root, shot, _protector));
    }

    [Fact]
    public void At_picks_the_nearest_focused_screenshot_within_the_tolerance()
    {
        Capture(0, "a.png.enc");
        long near = Capture(10, "b.png.enc");
        Capture(11, "c.png.enc", focused: false);

        using var reader = new CaptureReader(_dbPath);

        Assert.Equal(near, reader.FindScreenshotNear(T0.AddMinutes(9), TimeSpan.FromMinutes(5))!.WindowId);
        Assert.Null(reader.FindScreenshotNear(T0.AddMinutes(30), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Titles_come_back_scrubbed()
    {
        long id = Capture(0, "a.png.enc", title: "token=ghp_abcdefghijklmnopqrstuvwxyz0123 - Code");

        using var reader = new CaptureReader(_dbPath);

        Assert.DoesNotContain("ghp_", reader.FindScreenshot(id)!.WindowTitle);
    }

    [Fact]
    public void Images_outside_the_captures_folder_are_not_read()
    {
        File.WriteAllBytes(Path.Combine(_root, "elsewhere.png"), Png);
        using var reader = new CaptureReader(_dbPath);
        var shot = new StoredScreenshot(1, T0, "x", "y", true, @"..\elsewhere.png", T0, "y");

        Assert.Throws<IOException>(() => reader.ReadUnredactedImage(_root, shot, _protector));
    }

    [Fact]
    public void A_database_of_another_schema_is_refused()
    {
        string path = Path.Combine(_root, "old.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=4;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new CaptureReader(path));
    }
}
