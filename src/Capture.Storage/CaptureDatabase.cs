using System.Globalization;
using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Monitoring;
using Capture.Core.Redaction;
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
    long? DedupeReferenceId);

/// <summary>
/// The SQLite database. WAL mode and short transactions, so a crash or kill cannot corrupt it.
/// All members are safe to call from several threads.
///
/// Every window title passes through the title scrubber on its way in, so no title reaches the
/// disk without the secret check. Text recognized in screenshots is never stored.
/// </summary>
public sealed class CaptureDatabase : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Schema version. Databases of earlier versions (which stored recognized text) are not
    /// migrated: the app refuses to open them, and they have to be deleted.
    /// </summary>
    public const int SchemaVersion = 6;

    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;
    private readonly TextScrubber _titleScrubber;

    public CaptureDatabase(string path, TextScrubber titleScrubber)
    {
        _titleScrubber = titleScrubber;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        _connection.Open();

        long version = (long)Scalar("PRAGMA user_version;")!;
        if (version is > 0 and < SchemaVersion)
        {
            _connection.Dispose();
            throw new InvalidDataException(
                $"{path} was created by an earlier version of the app (schema {version}) and may contain recognized "
                + "screen text. It is not migrated: delete it (with its -wal and -shm files) and start again.");
        }

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA foreign_keys=ON;");
        // Deleted rows (day keys, text) are overwritten, not just unlinked.
        Execute("PRAGMA secure_delete=ON;");
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
                dedupe_ref_id    INTEGER REFERENCES capture_windows(id)
            );
            CREATE INDEX IF NOT EXISTS ix_capture_windows_capture ON capture_windows(capture_id);

            -- One encryption key per day for the stored images, wrapped with DPAPI (never in the
            -- clear). Retention deletes the rows of expired days, which makes their images unrecoverable.
            CREATE TABLE IF NOT EXISTS day_keys (
                day         TEXT PRIMARY KEY,   -- local date, yyyy-MM-dd
                wrapped_key BLOB NOT NULL
            );

            PRAGMA user_version=6;
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
            command.Parameters.AddWithValue("$title", (object?)_titleScrubber.Scrub(e.WindowTitle) ?? DBNull.Value);
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
                        z_index, is_focused, monitor_id, width, height, dhash, file_path, dedupe_ref_id)
                    VALUES (
                        $capture, $handle, $process, $title,
                        $left, $top, $right, $bottom,
                        $vleft, $vtop, $vright, $vbottom, $vfraction,
                        $z, $focused, $monitor, $width, $height, $dhash, $path, $ref)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$capture", captureId);
                command.Parameters.AddWithValue("$handle", info.Handle);
                command.Parameters.AddWithValue("$process", info.ProcessName ?? "");
                command.Parameters.AddWithValue("$title", _titleScrubber.Scrub(info.Title) ?? "");
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

    /// <summary>The wrapped key of <paramref name="day"/>, or null if there is none.</summary>
    public byte[]? GetWrappedDayKey(DateOnly day)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT wrapped_key FROM day_keys WHERE day = $day;";
            command.Parameters.AddWithValue("$day", DayText(day));
            return command.ExecuteScalar() as byte[];
        }
    }

    /// <summary>Stores the wrapped key unless the day has one already; returns the one that is stored.</summary>
    public byte[] InsertDayKeyIfMissing(DateOnly day, byte[] wrappedKey)
    {
        lock (_lock)
        {
            using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.CommandText = "INSERT OR IGNORE INTO day_keys (day, wrapped_key) VALUES ($day, $key);";
                insert.Parameters.AddWithValue("$day", DayText(day));
                insert.Parameters.AddWithValue("$key", wrappedKey);
                insert.ExecuteNonQuery();
            }
            using SqliteCommand select = _connection.CreateCommand();
            select.CommandText = "SELECT wrapped_key FROM day_keys WHERE day = $day;";
            select.Parameters.AddWithValue("$day", DayText(day));
            return (byte[])select.ExecuteScalar()!;
        }
    }

    /// <summary>Deletes the keys of all days before <paramref name="firstDayToKeep"/>. Returns the number of keys removed.</summary>
    public int DeleteDayKeysBefore(DateOnly firstDayToKeep)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM day_keys WHERE day < $day;";
            command.Parameters.AddWithValue("$day", DayText(firstDayToKeep));
            return command.ExecuteNonQuery();
        }
    }

    private static string DayText(DateOnly day) => day.ToString(FileImageStore.DayFolderFormat, CultureInfo.InvariantCulture);

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

    private object? Scalar(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
