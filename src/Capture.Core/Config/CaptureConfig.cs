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

    public List<AllowlistRule> Allowlist { get; set; } =
    [
        new() { Process = "Code.exe" },
        new() { Process = "WindowsTerminal.exe" },
    ];

    public int RetentionDays { get; set; } = 14;

    public int ImageQuality { get; set; } = 40;

    /// <summary>
    /// Saved images are scaled down so their longer side is at most this many pixels. 0 keeps the
    /// original size. Smaller images need less memory, CPU and disk, but small text gets harder to read.
    /// </summary>
    public int MaxImageDimension { get; set; } = 1920;

    /// <summary>AVIF encoder effort: 0 gives the smallest files, 9 is the fastest.</summary>
    public int ImageEncoderSpeed { get; set; } = 8;

    /// <summary>Maximum dHash Hamming distance at which two composites count as the same image.</summary>
    public int DedupeThreshold { get; set; } = 4;
}

public sealed class AllowlistRule
{
    /// <summary>Executable name, e.g. "Code.exe". Case-insensitive; the ".exe" suffix is optional.</summary>
    public string Process { get; set; } = "";

    /// <summary>
    /// Optional window title patterns with * and ? wildcards. If present, at least one must match the whole title.
    /// </summary>
    public List<string>? TitlePatterns { get; set; }
}
