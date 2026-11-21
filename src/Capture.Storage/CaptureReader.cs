using System.Globalization;
using Capture.Core.Export;
using Capture.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace Capture.Storage;

/// <summary>A captured window with an image file, as the screenshot command needs it.</summary>
/// <param name="WindowId">capture_windows.id of the window that was asked for.</param>
/// <param name="RelativePath">The image file, which may belong to an earlier window this one duplicates.</param>
public sealed record StoredScreenshot(
    long WindowId, DateTimeOffset Timestamp, string ProcessName, string WindowTitle, bool IsFocused, string RelativePath);

/// <summary>
/// Read-only access to the database for the command line tool. Opens the file read-only, so it
/// can run while TimeCapture is writing. Window titles come back as stored, that is, scrubbed.
/// </summary>
public sealed class CaptureReader : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>How far back to look for the events that set the state at the start of a range.</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    private readonly SqliteConnection _connection;

    public CaptureReader(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} does not exist. Has TimeCapture run with this storage root?");
        }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        _connection.Open();

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        if (version != CaptureDatabase.SchemaVersion)
        {
            _connection.Dispose();
            throw new InvalidDataException(
                $"{path} has schema {version}, this tool reads schema {CaptureDatabase.SchemaVersion}. Update TimeCapture and this tool together.");
        }
    }

    /// <summary>The events of the range, plus those of the days before it so the state at its start is known.</summary>
    public IReadOnlyList<TimelineEvent> ReadEvents(DateTimeOffset from, DateTimeOffset to)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, type, process_name, window_title FROM events
            WHERE timestamp >= $from AND timestamp < $to ORDER BY timestamp, id;
            """;
        command.Parameters.AddWithValue("$from", Format(from - Lookback));
        command.Parameters.AddWithValue("$to", Format(to));

        var events = new List<TimelineEvent>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (EventTypeNames.FromDbName(reader.GetString(1)) is { } type)
            {
                events.Add(new TimelineEvent(
                    Parse(reader.GetString(0)), type,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        return events;
    }

    /// <summary>The time of the last event or capture before <paramref name="to"/>, or null if there is none.</summary>
    public DateTimeOffset? LastRecordBefore(DateTimeOffset to)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT max(t) FROM (
                SELECT max(timestamp) AS t FROM events WHERE timestamp < $to
                UNION ALL
                SELECT max(timestamp) FROM captures WHERE timestamp < $to);
            """;
        command.Parameters.AddWithValue("$to", Format(to));
        return command.ExecuteScalar() is string value ? Parse(value) : null;
    }

    /// <summary>
    /// The focused windows in the range that have an image file of their own. Duplicates of an
    /// earlier image are left out: they show what that one shows.
    /// </summary>
    public IReadOnlyList<ScreenshotRef> ReadFocusedScreenshots(DateTimeOffset from, DateTimeOffset to)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT c.timestamp, w.id, w.process_name
            FROM capture_windows w JOIN captures c ON c.id = w.capture_id
            WHERE c.timestamp >= $from AND c.timestamp < $to AND w.is_focused = 1 AND w.file_path IS NOT NULL
            ORDER BY c.timestamp, w.id;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));

        var shots = new List<ScreenshotRef>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            shots.Add(new ScreenshotRef(Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2)));
        }
        return shots;
    }

    /// <summary>The image of a captured window, following a duplicate to the image it refers to. Null if there is none.</summary>
    public StoredScreenshot? FindScreenshot(long windowId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, c.timestamp, w.process_name, w.window_title, w.is_focused, coalesce(w.file_path, r.file_path)
            FROM capture_windows w
            JOIN captures c ON c.id = w.capture_id
            LEFT JOIN capture_windows r ON r.id = w.dedupe_ref_id
            WHERE w.id = $id;
            """;
        command.Parameters.AddWithValue("$id", windowId);
        return ReadScreenshot(command);
    }

    /// <summary>
    /// The image of the focused window captured closest to <paramref name="time"/>, at most
    /// <paramref name="tolerance"/> away. Null if there is none.
    /// </summary>
    public StoredScreenshot? FindScreenshotNear(DateTimeOffset time, TimeSpan tolerance)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, c.timestamp, w.process_name, w.window_title, w.is_focused, coalesce(w.file_path, r.file_path)
            FROM capture_windows w
            JOIN captures c ON c.id = w.capture_id
            LEFT JOIN capture_windows r ON r.id = w.dedupe_ref_id
            WHERE c.timestamp >= $from AND c.timestamp <= $to AND w.is_focused = 1
              AND coalesce(w.file_path, r.file_path) IS NOT NULL
            ORDER BY abs(julianday(c.timestamp) - julianday($at)), w.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$from", Format(time - tolerance));
        command.Parameters.AddWithValue("$to", Format(time + tolerance));
        command.Parameters.AddWithValue("$at", Format(time));
        return ReadScreenshot(command);
    }

    /// <summary>The wrapped key of <paramref name="day"/>, or null if there is none.</summary>
    public byte[]? GetWrappedDayKey(DateOnly day)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT wrapped_key FROM day_keys WHERE day = $day;";
        command.Parameters.AddWithValue("$day", day.ToString(FileImageStore.DayFolderFormat, CultureInfo.InvariantCulture));
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>
    /// The screenshot's image, decrypted, NOT redacted. For on-demand text recognition and
    /// redaction only: nothing else may see these bytes.
    /// </summary>
    internal byte[] ReadUnredactedImage(string root, StoredScreenshot screenshot, IKeyProtector protector) =>
        FileImageStore.ReadUnredacted(root, screenshot.RelativePath,
            day => GetWrappedDayKey(day) is { } wrapped ? protector.Unprotect(wrapped) : null);

    public void Dispose() => _connection.Dispose();

    private static StoredScreenshot? ReadScreenshot(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(5))
        {
            return null;
        }
        return new StoredScreenshot(
            reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4) != 0, reader.GetString(5));
    }

    private static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
