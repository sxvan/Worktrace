using System.Globalization;
using Capture.Core.Export;
using Capture.Core.Monitoring;
using Capture.Core.Redaction;
using Microsoft.Data.Sqlite;

namespace Capture.Storage;

/// <summary>A captured window that has an image file.</summary>
public sealed record StoredImage(long WindowId, DateTimeOffset Timestamp, string ProcessName, string WindowTitle, string RelativePath);

/// <summary>
/// Read-only access to the database for exports. Opens the file read-only, so it can run while
/// TimeCapture is writing.
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
            throw new FileNotFoundException($"{path} does not exist.");
        }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        _connection.Open();
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

    /// <summary>The recognized text of the focused window of each capture in the range.</summary>
    public IReadOnlyList<TextSample> ReadFocusedText(DateTimeOffset from, DateTimeOffset to)
    {
        using SqliteCommand command = _connection.CreateCommand();
        // A duplicate window has no text of its own: it shows what the window it refers to showed.
        command.CommandText = """
            SELECT c.timestamp, w.id, w.process_name, t.text, t.left, t.top, t.right, t.bottom
            FROM capture_windows w
            JOIN captures c ON c.id = w.capture_id
            JOIN capture_text t ON t.capture_window_id = coalesce(w.dedupe_ref_id, w.id)
            WHERE c.timestamp >= $from AND c.timestamp < $to AND w.is_focused = 1
            ORDER BY c.timestamp, w.id, t.line_index;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));

        var samples = new List<TextSample>();
        List<ScreenTextLine>? lines = null;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            long windowId = reader.GetInt64(1);
            if (samples.Count == 0 || samples[^1].WindowId != windowId)
            {
                lines = [];
                samples.Add(new TextSample(Parse(reader.GetString(0)), windowId, reader.GetString(2), lines));
            }
            lines!.Add(new ScreenTextLine(
                reader.GetString(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7)));
        }
        return samples;
    }

    /// <summary>The focused windows in the range that have an image file of their own.</summary>
    public IReadOnlyList<ImageRef> ReadFocusedImages(DateTimeOffset from, DateTimeOffset to)
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

        var images = new List<ImageRef>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            images.Add(new ImageRef(Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2)));
        }
        return images;
    }

    /// <summary>The image of a captured window, following a duplicate to the image it refers to. Null if there is none.</summary>
    public StoredImage? FindImage(long windowId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, c.timestamp, w.process_name, w.window_title, coalesce(w.file_path, r.file_path)
            FROM capture_windows w
            JOIN captures c ON c.id = w.capture_id
            LEFT JOIN capture_windows r ON r.id = w.dedupe_ref_id
            WHERE w.id = $id;
            """;
        command.Parameters.AddWithValue("$id", windowId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(4))
        {
            return null;
        }
        return new StoredImage(
            reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    public void Dispose() => _connection.Dispose();

    private static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
