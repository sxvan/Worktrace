using Capture.Core.Capturing;

namespace Capture.Core.Pipeline;

/// <summary>
/// One monitor's picture straight from capture: black canvas with only the visible parts of
/// allowed windows. Not yet redacted, so it cannot be saved; see <see cref="RedactedComposite"/>.
/// Disposing clears the pixel buffer.
/// </summary>
public sealed class RawComposite(
    DateTimeOffset timestamp,
    MonitorInfo monitor,
    int width,
    int height,
    byte[] pixels,
    IReadOnlyList<WindowPlacement> windows) : IDisposable
{
    private byte[]? _pixels = pixels;

    public DateTimeOffset Timestamp { get; } = timestamp;
    public MonitorInfo Monitor { get; } = monitor;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyList<WindowPlacement> Windows { get; } = windows;

    /// <summary>BGRA, rows top to bottom.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(RawComposite));

    /// <summary>Hands the buffer to the redaction stage; this object no longer owns or clears it.</summary>
    internal byte[] DetachPixels()
    {
        byte[] pixels = Pixels;
        _pixels = null;
        return pixels;
    }

    public void Dispose()
    {
        if (_pixels is not null)
        {
            Array.Clear(_pixels);
            _pixels = null;
        }
    }
}

/// <summary>
/// A composite that has passed the redaction stage. This is the only type the image store accepts,
/// and only redactors in this assembly can create it. Disposing clears the pixel buffer.
/// </summary>
public sealed class RedactedComposite : IDisposable
{
    private byte[]? _pixels;

    internal RedactedComposite(
        DateTimeOffset timestamp,
        MonitorInfo monitor,
        int width,
        int height,
        byte[] pixels,
        IReadOnlyList<WindowPlacement> windows)
    {
        Timestamp = timestamp;
        Monitor = monitor;
        Width = width;
        Height = height;
        _pixels = pixels;
        Windows = windows;
    }

    public DateTimeOffset Timestamp { get; }
    public MonitorInfo Monitor { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<WindowPlacement> Windows { get; }

    /// <summary>BGRA, rows top to bottom.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(RedactedComposite));

    public void Dispose()
    {
        if (_pixels is not null)
        {
            Array.Clear(_pixels);
            _pixels = null;
        }
    }
}

public enum CaptureOutcome
{
    /// <summary>Composites were produced.</summary>
    Captured,

    /// <summary>No allowed window is visible on any monitor: nothing was captured.</summary>
    NoAllowedWindow,

    /// <summary>Something was uncertain (error, window layout changed mid-capture): the frame was thrown away.</summary>
    Discarded,
}

/// <param name="Reason">Technical reason for a discard. Never contains titles or content.</param>
public sealed record CaptureResult(CaptureOutcome Outcome, IReadOnlyList<RawComposite> Composites, string? Reason = null)
{
    public static CaptureResult NoAllowedWindow { get; } = new(CaptureOutcome.NoAllowedWindow, []);

    public static CaptureResult Discarded(string reason) => new(CaptureOutcome.Discarded, [], reason);
}
