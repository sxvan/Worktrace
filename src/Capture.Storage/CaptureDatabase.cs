using System.Globalization;
using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Monitoring;
using Capture.Core.Triggers;
using Microsoft.Data.Sqlite;

namespace Capture.Storage;

/// <summary>One capturable window of a capture, as stored in capture_windows.</summary>
/// <param name="FilePath">Relative path of the saved image, or null if it duplicates an earlier one.</param>
/// <param name="DedupeReferenceId">capture_windows.id of the earlier image this one duplicates.</param>
public sealed record CapturedWindowRecord(
    WindowPlacement Placement,
    bool IsFocused,
    int Width,
    int Height,
    ulong DHash,
    string? FilePath,
    long? DedupeReferenceId,
    int RedactionCount = 0);

/// <summary>Summary of a stored capture for display.</summary>
public sealed record CaptureSummary(DateTimeOffset Timestamp, string Trigger, int Windows, int SavedImages);

/// <summary>
/// The SQLite database. WAL mode and short transactions, so a crash or kill cannot corrupt it.
/// All members are safe to call from several threads.
/// </summary>
public sealed class CaptureDatabase : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;

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
        Execute("PRAGMA foreign_keys=ON;");
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                id            INTEGER PRIMARY KEY,
                timestamp     TEXT NOT NULL,   -- UTC, ISO 8601
                type          TEXT NOT NULL,
                process_name  TEXT,            -- null unless a rule covers the window
                window_title  TEXT,            -- null unless a rule covers the window
                window_handle INTEGER          -- null unless a rule covers the window
            );
            CREATE INDEX IF NOT EXISTS ix_events_timestamp ON events(timestamp);

            -- One row per distinct monitor configuration ever seen.
            CREATE TABLE IF NOT EXISTS monitors (
                id          INTEGER PRIMARY KEY,
                device_name TEXT NOT NULL,
                left        INTEGER NOT NULL,
                top         INTEGER NOT NULL,
                right       INTEGER NOT NULL,
                bottom      INTEGER NOT NULL,
                dpi_scale   REAL NOT NULL,
                is_primary  INTEGER NOT NULL,
                UNIQUE (device_name, left, top, right, bottom, dpi_scale, is_primary)
            );

            -- One row per capture that recorded at least one capturable window.
            CREATE TABLE IF NOT EXISTS captures (
                id                    INTEGER PRIMARY KEY,
                timestamp             TEXT NOT NULL,   -- UTC, ISO 8601
                trigger               TEXT NOT NULL,
                focused_window_handle INTEGER          -- null if the focused window is not a captured one
            );
            CREATE INDEX IF NOT EXISTS ix_captures_timestamp ON captures(timestamp);

            -- One row, and at most one image, per capturable window per capture.
            CREATE TABLE IF NOT EXISTS capture_windows (
                id               INTEGER PRIMARY KEY,
                capture_id       INTEGER NOT NULL REFERENCES captures(id),
                window_handle    INTEGER NOT NULL,
                process_name     TEXT NOT NULL,
                window_title     TEXT NOT NULL,
                left             INTEGER NOT NULL,   -- window bounds, virtual-screen pixels
                top              INTEGER NOT NULL,
                right            INTEGER NOT NULL,
                bottom           INTEGER NOT NULL,
                visible_left     INTEGER NOT NULL,   -- bounding box of the visible parts
                visible_top      INTEGER NOT NULL,
                visible_right    INTEGER NOT NULL,
                visible_bottom   INTEGER NOT NULL,
                visible_fraction REAL NOT NULL,      -- share of the window that was visible, 0..1
                z_index          INTEGER NOT NULL,   -- 0 = topmost of all windows
                is_focused       INTEGER NOT NULL,
                monitor_id       INTEGER NOT NULL REFERENCES monitors(id),
                width            INTEGER NOT NULL,   -- image size
                height           INTEGER NOT NULL,
                dhash            INTEGER NOT NULL,
                file_path        TEXT,               -- null if deduplicated or removed by retention
                dedupe_ref_id    INTEGER REFERENCES capture_windows(id),
                redaction_count  INTEGER NOT NULL DEFAULT 0   -- regions blacked out by redaction
            );
            CREATE INDEX IF NOT EXISTS ix_capture_windows_capture ON capture_windows(capture_id);

            PRAGMA user_version=3;
            """);
    }

    public void InsertEvent(ActivityEvent e)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO events (timestamp, type, process_name, window_title, window_handle)
                VALUES ($timestamp, $type, $process, $title, $handle);
                """;
            command.Parameters.AddWithValue("$timestamp", Format(e.Timestamp));
            command.Parameters.AddWithValue("$type", e.Type.ToDbName());
            command.Parameters.AddWithValue("$process", (object?)e.ProcessName ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", (object?)e.WindowTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$handle", (object?)e.WindowHandle ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Stores one capture with its windows in a single transaction. Returns the capture_windows
    /// ids in the order of <paramref name="windows"/>.
    /// </summary>
    public IReadOnlyList<long> InsertCapture(
        DateTimeOffset timestamp, TriggerType trigger, long? focusedWindowHandle, IReadOnlyList<CapturedWindowRecord> windows)
    {
        lock (_lock)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();

            long captureId;
            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO captures (timestamp, trigger, focused_window_handle)
                    VALUES ($timestamp, $trigger, $focused) RETURNING id;
                    """;
                command.Parameters.AddWithValue("$timestamp", Format(timestamp));
                command.Parameters.AddWithValue("$trigger", trigger.ToDbName());
                command.Parameters.AddWithValue("$focused", (object?)focusedWindowHandle ?? DBNull.Value);
                captureId = (long)command.ExecuteScalar()!;
            }

            var ids = new List<long>(windows.Count);
            foreach (CapturedWindowRecord window in windows)
            {
                WindowInfo info = window.Placement.Window;
                RectInt visible = window.Placement.VisibleBounds;
                long monitorId = GetOrAddMonitor(window.Placement.Monitor, transaction);

                using SqliteCommand command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO capture_windows (
                        capture_id, window_handle, process_name, window_title,
                        left, top, right, bottom,
                        visible_left, visible_top, visible_right, visible_bottom, visible_fraction,
                        z_index, is_focused, monitor_id, width, height, dhash, file_path, dedupe_ref_id, redaction_count)
                    VALUES (
                        $capture, $handle, $process, $title,
                        $left, $top, $right, $bottom,
                        $vleft, $vtop, $vright, $vbottom, $vfraction,
                        $z, $focused, $monitor, $width, $height, $dhash, $path, $ref, $redactions)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$capture", captureId);
                command.Parameters.AddWithValue("$handle", info.Handle);
                command.Parameters.AddWithValue("$process", info.ProcessName ?? "");
                command.Parameters.AddWithValue("$title", info.Title ?? "");
                command.Parameters.AddWithValue("$left", info.Bounds.Left);
                command.Parameters.AddWithValue("$top", info.Bounds.Top);
                command.Parameters.AddWithValue("$right", info.Bounds.Right);
                command.Parameters.AddWithValue("$bottom", info.Bounds.Bottom);
                command.Parameters.AddWithValue("$vleft", visible.Left);
                command.Parameters.AddWithValue("$vtop", visible.Top);
                command.Parameters.AddWithValue("$vright", visible.Right);
                command.Parameters.AddWithValue("$vbottom", visible.Bottom);
                command.Parameters.AddWithValue("$vfraction", window.Placement.VisibleFraction);
                command.Parameters.AddWithValue("$z", info.ZIndex);
                command.Parameters.AddWithValue("$focused", window.IsFocused ? 1 : 0);
                command.Parameters.AddWithValue("$monitor", monitorId);
                command.Parameters.AddWithValue("$width", window.Width);
                command.Parameters.AddWithValue("$height", window.Height);
                command.Parameters.AddWithValue("$dhash", unchecked((long)window.DHash));
                command.Parameters.AddWithValue("$path", (object?)window.FilePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$ref", (object?)window.DedupeReferenceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$redactions", window.RedactionCount);
                ids.Add((long)command.ExecuteScalar()!);
            }

            transaction.Commit();
            return ids;
        }
    }

    /// <summary>
    /// Forgets the image files of all day folders before <paramref name="firstDayToKeep"/>:
    /// rows stay, their file path becomes null. Returns the number of rows changed.
    /// </summary>
    public int ClearFilePathsBefore(DateOnly firstDayToKeep)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            // file_path is "captures\yyyy-MM-dd\name": the day starts at character 10.
            command.CommandText = """
                UPDATE capture_windows SET file_path = NULL
                WHERE file_path IS NOT NULL AND substr(file_path, 10, 10) < $day;
                """;
            command.Parameters.AddWithValue("$day", firstDayToKeep.ToString(FileImageStore.DayFolderFormat, CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>The most recent events, oldest first.</summary>
    public IReadOnlyList<ActivityEvent> ReadLastEvents(int count)
    {
        lock (_lock)
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
                    Parse(reader.GetString(0)),
                    EventTypeNames.FromDbName(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            }
            result.Reverse();
            return result;
        }
    }

    /// <summary>The most recent captures, oldest first.</summary>
    public IReadOnlyList<CaptureSummary> ReadLastCaptures(int count)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT c.timestamp, c.trigger, COUNT(w.id), COUNT(w.file_path)
                FROM captures c LEFT JOIN capture_windows w ON w.capture_id = c.id
                GROUP BY c.id ORDER BY c.id DESC LIMIT $count;
                """;
            command.Parameters.AddWithValue("$count", count);

            var result = new List<CaptureSummary>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CaptureSummary(Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            }
            result.Reverse();
            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }

    private long GetOrAddMonitor(MonitorInfo monitor, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO monitors (device_name, left, top, right, bottom, dpi_scale, is_primary)
            VALUES ($device, $left, $top, $right, $bottom, $scale, $primary);
            SELECT id FROM monitors
            WHERE device_name = $device AND left = $left AND top = $top AND right = $right AND bottom = $bottom
              AND dpi_scale = $scale AND is_primary = $primary;
            """;
        command.Parameters.AddWithValue("$device", monitor.DeviceName);
        command.Parameters.AddWithValue("$left", monitor.Bounds.Left);
        command.Parameters.AddWithValue("$top", monitor.Bounds.Top);
        command.Parameters.AddWithValue("$right", monitor.Bounds.Right);
        command.Parameters.AddWithValue("$bottom", monitor.Bounds.Bottom);
        command.Parameters.AddWithValue("$scale", monitor.Scale);
        command.Parameters.AddWithValue("$primary", monitor.IsPrimary ? 1 : 0);
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
