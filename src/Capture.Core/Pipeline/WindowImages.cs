using Capture.Core.Capturing;

namespace Capture.Core.Pipeline;

/// <summary>
/// The picture of one capturable window straight from capture: the window's own size, showing only
/// the parts that were visible on screen, the rest black. Saved as it is (encrypted); it is never
/// redacted at capture time. Disposing clears the pixel buffer.
/// </summary>
public sealed class RawWindowImage(
    DateTimeOffset timestamp,
    WindowPlacement placement,
    int width,
    int height,
    byte[] pixels) : IDisposable
{
    private byte[]? _pixels = pixels;

    public DateTimeOffset Timestamp { get; } = timestamp;
    public WindowPlacement Placement { get; } = placement;
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>BGRA, rows top to bottom.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(RawWindowImage));

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
    /// <summary>Window images were produced.</summary>
    Captured,

    /// <summary>No capturable window is visible on any monitor: nothing was captured.</summary>
    NoCapturableWindow,

    /// <summary>Something was uncertain (error, window layout changed mid-capture): the frame was thrown away.</summary>
    Discarded,
}

/// <param name="Images">One image per visible capturable window, topmost first. The receiver must dispose them.</param>
/// <param name="FocusedWindowHandle">The foreground window, if it is one of the captured windows.</param>
/// <param name="Reason">Technical reason for a discard. Never contains titles or content.</param>
public sealed record CaptureResult(
    CaptureOutcome Outcome,
    IReadOnlyList<RawWindowImage> Images,
    long? FocusedWindowHandle = null,
    string? Reason = null)
{
    public static CaptureResult NoCapturableWindow { get; } = new(CaptureOutcome.NoCapturableWindow, []);

    public static CaptureResult Discarded(string reason) => new(CaptureOutcome.Discarded, [], Reason: reason);
}
