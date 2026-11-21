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
/// <param name="Text">Recognized text to store with the window, already scrubbed; null stores none.</param>
public sealed record CapturedWindowRecord(
    WindowPlacement Placement,
    bool IsFocused,
    int Width,
    int Height,
    ulong DHash,
    string? FilePath,
    long? DedupeReferenceId,
    int RedactionCount = 0,
    IReadOnlyList<ScreenTextLine>? Text = null);

/// <summary>
/// The SQLite database. WAL mode and short transactions, so a crash or kill cannot corrupt it.
/// All members are safe to call from several threads.
///
/// Every window title passes through the title scrubber on its way in, so no title reaches the
/// disk without the secret check.
/// </summary>
public sealed class CaptureDatabase : IDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

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
                dedupe_ref_id    INTEGER REFERENCES capture_windows(id),
                redaction_count  INTEGER NOT NULL DEFAULT 0   -- regions blacked out by redaction
            );
            CREATE INDEX IF NOT EXISTS ix_capture_windows_capture ON capture_windows(capture_id);

            -- Recognized text of a captured window, secrets replaced (layer 2). One row per text
            -- segment; the box is relative to the window image, 0..1.
            CREATE TABLE IF NOT EXISTS capture_text (
                id                INTEGER PRIMARY KEY,
                capture_window_id INTEGER NOT NULL REFERENCES capture_windows(id),
                line_index        INTEGER NOT NULL,
                text              TEXT NOT NULL,
                left              REAL NOT NULL,
                top               REAL NOT NULL,
                right             REAL NOT NULL,
                bottom            REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_capture_text_window ON capture_text(capture_window_id);

            -- One encryption key per day for the stored images, wrapped with DPAPI (never in the
            -- clear). Retention deletes the rows of expired days, which makes their images unrecoverable.
            CREATE TABLE IF NOT EXISTS day_keys (
                day         TEXT PRIMARY KEY,   -- local date, yyyy-MM-dd
                wrapped_key BLOB NOT NULL
            );

            PRAGMA user_version=5;
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
                command.Parameters.AddWithValue("$redactions", window.RedactionCount);
                long windowId = (long)command.ExecuteScalar()!;
                ids.Add(windowId);

                if (window.Text is not null)
                {
                    InsertText(windowId, window.Text, transaction);
                }
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

    /// <summary>Deletes the recognized text of all captures before <paramref name="cutoff"/>. Returns the number of rows removed.</summary>
    public int DeleteTextBefore(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM capture_text WHERE capture_window_id IN (
                    SELECT w.id FROM capture_windows w JOIN captures c ON c.id = w.capture_id
                    WHERE c.timestamp < $cutoff);
                """;
            command.Parameters.AddWithValue("$cutoff", Format(cutoff));
            return command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }

    private void InsertText(long windowId, IReadOnlyList<ScreenTextLine> lines, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_text (capture_window_id, line_index, text, left, top, right, bottom)
            VALUES ($window, $index, $text, $left, $top, $right, $bottom);
            """;
        SqliteParameter index = command.Parameters.Add("$index", SqliteType.Integer);
        SqliteParameter text = command.Parameters.Add("$text", SqliteType.Text);
        SqliteParameter left = command.Parameters.Add("$left", SqliteType.Real);
        SqliteParameter top = command.Parameters.Add("$top", SqliteType.Real);
        SqliteParameter right = command.Parameters.Add("$right", SqliteType.Real);
        SqliteParameter bottom = command.Parameters.Add("$bottom", SqliteType.Real);
        command.Parameters.AddWithValue("$window", windowId);

        for (int i = 0; i < lines.Count; i++)
        {
            index.Value = i;
            text.Value = lines[i].Text;
            left.Value = lines[i].Left;
            top.Value = lines[i].Top;
            right.Value = lines[i].Right;
            bottom.Value = lines[i].Bottom;
            command.ExecuteNonQuery();
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
}
