namespace Capture.Core.Config;

/// <summary>All user-tunable settings. Serialized as config.json.</summary>
public sealed class CaptureConfig
{
    /// <summary>Folder for the database, captures and logs. Null means the default app data folder.</summary>
    public string? StorageRoot { get; set; }

    /// <summary>How often input activity is polled.</summary>
    public int InputPollIntervalMs { get; set; } = 1000;

    /// <summary>Delay after a window switch or title change before capturing.</summary>
    public int WindowSwitchDelayMs { get; set; } = 1000;

    /// <summary>Quiet time after input that triggers a capture.</summary>
    public int TypingPauseMs { get; set; } = 2000;

    /// <summary>Capture at least this often while the user is active.</summary>
    public int FallbackIntervalSeconds { get; set; } = 60;

    /// <summary>Minimum time between two captures.</summary>
    public int MinCaptureIntervalSeconds { get; set; } = 5;

    /// <summary>No input for this long counts as idle.</summary>
    public int IdleTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// What may be recorded. The strongest matching include rule grants its level; windows that match
    /// none are logged only as "other", without process name or title, and are never captured. There
    /// are no built-in rules: until config.json lists some, nothing is recorded.
    /// </summary>
    public List<WindowRule> Include { get; set; } = [];

    /// <summary>
    /// What may never be recorded. A matching exclude rule takes its level and everything above it
    /// away, whatever the include rules say.
    /// </summary>
    public List<WindowRule> Exclude { get; set; } = [];

    /// <summary>Screenshots older than this many days are deleted.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Save the screenshots themselves (layer 2). Off keeps window titles and timing only.
    /// </summary>
    public bool StoreImages { get; set; } = true;

    /// <summary>
    /// Encrypt the saved screenshots (AES-GCM, one key per day, keys protected by the Windows
    /// login through DPAPI). Off writes plain PNG files; images encrypted earlier stay readable.
    /// </summary>
    public bool EncryptImages { get; set; } = true;

    /// <summary>
    /// Cut the query string and fragment off URLs in stored window titles. The secret check
    /// already replaces values that look like secrets; this also drops the ones it does not
    /// recognize, at the cost of ids some tools keep there.
    /// </summary>
    public bool StripUrlQueries { get; set; }

    /// <summary>How far back the tray menu's "Delete last minutes" reaches.</summary>
    public int DeleteRecentMinutes { get; set; } = 5;

    /// <summary>
    /// Whether "Delete last minutes" also deletes the activity events (window switches, idle and
    /// so on) of that span. Off deletes only the captures: their screenshots and window titles.
    /// </summary>
    public bool DeleteRecentEvents { get; set; }
}

public sealed class WindowRule
{
    /// <summary>Executable name, e.g. "Code.exe", or "*" for every application. Case-insensitive; the ".exe" suffix is optional.</summary>
    public string Process { get; set; } = "";

    /// <summary>
    /// A level, strongest first: "capture" (process, title and screenshots), "logWithTitles"
    /// (process and title) or "log" (process only). An include rule grants the level, an exclude
    /// rule takes it and everything above it away.
    /// </summary>
    public string Mode { get; set; } = "capture";

    /// <summary>
    /// Optional window title patterns with * and ? wildcards. If present, at least one must match the whole title.
    /// </summary>
    public List<string>? TitlePatterns { get; set; }
}
