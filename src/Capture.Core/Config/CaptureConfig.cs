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
    /// What may be recorded, per application. Windows that match no rule are logged only as "other",
    /// without process name or title, and are never captured. There are no built-in rules: until
    /// config.json lists some, nothing is recorded.
    /// </summary>
    public List<WindowRule> Rules { get; set; } = [];

    /// <summary>Screenshots older than this many days are deleted (layer 3).</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Save the screenshots themselves (layer 3). Off keeps window titles and recognized text only:
    /// text the recognizer missed is then gone instead of visible in an image.
    /// </summary>
    public bool StoreImages { get; set; } = true;

    /// <summary>
    /// Encrypt the saved screenshots (AES-GCM, one key per day, keys protected by the Windows
    /// login through DPAPI). Off writes plain PNG files; images encrypted earlier stay readable.
    /// </summary>
    public bool EncryptImages { get; set; } = true;

    /// <summary>
    /// Saved images (lossless PNG) are scaled down so their longer side is at most this many
    /// pixels. 0, the default, keeps the original size: scaled-down screen text soon becomes
    /// unreadable for text recognition.
    /// </summary>
    public int MaxImageDimension { get; set; }

    /// <summary>
    /// Cut the query string and fragment off URLs in stored window titles. The secret check
    /// already replaces values that look like secrets; this also drops the ones it does not
    /// recognize, at the cost of ids some tools keep there.
    /// </summary>
    public bool StripUrlQueries { get; set; }

    /// <summary>Take no capture while the focused element is a password field.</summary>
    public bool SkipPasswordFields { get; set; } = true;
}

public sealed class WindowRule
{
    /// <summary>Executable name, e.g. "Code.exe". Case-insensitive; the ".exe" suffix is optional.</summary>
    public string Process { get; set; } = "";

    /// <summary>
    /// "capture": log process and title, and take screenshots.
    /// "log": log that the application is used, but never take screenshots.
    /// </summary>
    public string Mode { get; set; } = "capture";

    /// <summary>For "log" rules: also store the window title. Leave off for password managers, mail and the like.</summary>
    public bool LogTitles { get; set; }

    /// <summary>
    /// Optional window title patterns with * and ? wildcards. If present, at least one must match the whole title.
    /// </summary>
    public List<string>? TitlePatterns { get; set; }

    /// <summary>
    /// Optional title patterns with * and ? wildcards for windows this rule does not cover, e.g.
    /// "*YouTube*". A window whose whole title matches one is left to the other rules, as if this
    /// rule did not exist; if none covers it, it is recorded as "other" only.
    /// </summary>
    public List<string>? ExcludeTitlePatterns { get; set; }
}
