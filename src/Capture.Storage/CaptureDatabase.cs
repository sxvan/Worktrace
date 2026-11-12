using System.Globalization;
using Capture.Core.Monitoring;
using Capture.Core.Triggers;
using Microsoft.Data.Sqlite;

namespace Capture.Storage;

/// <summary>
/// The SQLite database. WAL mode and one short transaction per write, so a crash or kill
/// cannot corrupt it. Not thread-safe: use from a single writer.
/// </summary>
public sealed class CaptureDatabase : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertEvent;

    public CaptureDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                id            INTEGER PRIMARY KEY,
                timestamp     TEXT NOT NULL,   -- UTC, ISO 8601
                type          TEXT NOT NULL,
                process_name  TEXT,            -- null unless the window is allowed
                window_title  TEXT,            -- null unless the window is allowed
                window_handle INTEGER          -- null unless the window is allowed
            );
            CREATE INDEX IF NOT EXISTS ix_events_timestamp ON events(timestamp);

            -- M2 only: "would capture now" decisions. Replaced by the captures table in M4.
            CREATE TABLE IF NOT EXISTS capture_decisions (
                id        INTEGER PRIMARY KEY,
                timestamp TEXT NOT NULL,       -- UTC, ISO 8601
                trigger   TEXT NOT NULL
            );
            PRAGMA user_version=1;
            """);

        _insertEvent = _connection.CreateCommand();
        _insertEvent.CommandText = """
            INSERT INTO events (timestamp, type, process_name, window_title, window_handle)
            VALUES ($timestamp, $type, $process, $title, $handle);
            """;
        _insertEvent.Parameters.Add("$timestamp", SqliteType.Text);
        _insertEvent.Parameters.Add("$type", SqliteType.Text);
        _insertEvent.Parameters.Add("$process", SqliteType.Text);
        _insertEvent.Parameters.Add("$title", SqliteType.Text);
        _insertEvent.Parameters.Add("$handle", SqliteType.Integer);
    }

    public void InsertEvent(ActivityEvent e)
    {
        _insertEvent.Parameters["$timestamp"].Value = e.Timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        _insertEvent.Parameters["$type"].Value = e.Type.ToDbName();
        _insertEvent.Parameters["$process"].Value = (object?)e.ProcessName ?? DBNull.Value;
        _insertEvent.Parameters["$title"].Value = (object?)e.WindowTitle ?? DBNull.Value;
        _insertEvent.Parameters["$handle"].Value = (object?)e.WindowHandle ?? DBNull.Value;
        _insertEvent.ExecuteNonQuery();
    }

    /// <summary>The most recent events, oldest first.</summary>
    public IReadOnlyList<ActivityEvent> ReadLastEvents(int count)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, type, process_name, window_title, window_handle
            FROM events ORDER BY id DESC LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);

        var result = new List<ActivityEvent>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityEvent(
                DateTimeOffset.ParseExact(reader.GetString(0), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                EventTypeNames.FromDbName(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        result.Reverse();
        return result;
    }

    public void InsertCaptureDecision(CaptureDecision decision)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO capture_decisions (timestamp, trigger) VALUES ($timestamp, $trigger);";
        command.Parameters.AddWithValue("$timestamp", decision.Timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$trigger", decision.Trigger.ToDbName());
        command.ExecuteNonQuery();
    }

    /// <summary>The most recent capture decisions as (timestamp, trigger name), oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset Timestamp, string Trigger)> ReadLastCaptureDecisions(int count)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT timestamp, trigger FROM capture_decisions ORDER BY id DESC LIMIT $count;";
        command.Parameters.AddWithValue("$count", count);

        var result = new List<(DateTimeOffset, string)>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                DateTimeOffset.ParseExact(reader.GetString(0), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                reader.GetString(1)));
        }
        result.Reverse();
        return result;
    }

    public void Dispose()
    {
        _insertEvent.Dispose();
        _connection.Dispose();
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
