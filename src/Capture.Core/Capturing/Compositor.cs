using Capture.Core.Geometry;

namespace Capture.Core.Capturing;

/// <summary>Draws window pixels onto a monitor canvas. All buffers are 32-bit BGRA, rows top to bottom, no padding.</summary>
public static class Compositor
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Copies the given visible rectangles of a window frame onto the canvas of a monitor.
    /// Throws if any rectangle lies outside the frame or the monitor: a caller that sees an
    /// exception must discard the whole capture.
    /// </summary>
    /// <param name="canvas">Monitor canvas, <paramref name="monitorBounds"/> in size.</param>
    /// <param name="frame">Window frame pixels.</param>
    /// <param name="frameOrigin">Virtual-screen position of the frame's top-left pixel.</param>
    /// <param name="visibleRects">Virtual-screen rectangles to copy.</param>
    public static void Blit(
        byte[] canvas,
        RectInt monitorBounds,
        byte[] frame,
        int frameWidth,
        int frameHeight,
        (int X, int Y) frameOrigin,
        IReadOnlyList<RectInt> visibleRects)
    {
        if (canvas.Length != (long)monitorBounds.Width * monitorBounds.Height * BytesPerPixel)
        {
            throw new ArgumentException("Canvas size does not match the monitor bounds.", nameof(canvas));
        }
        if (frame.Length != (long)frameWidth * frameHeight * BytesPerPixel)
        {
            throw new ArgumentException("Frame buffer size does not match the frame dimensions.", nameof(frame));
        }

        var frameBounds = RectInt.FromSize(frameOrigin.X, frameOrigin.Y, frameWidth, frameHeight);

        foreach (RectInt rect in visibleRects)
        {
            if (!monitorBounds.Contains(rect) || !frameBounds.Contains(rect))
            {
                throw new InvalidOperationException("Visible rectangle lies outside the monitor or the window frame.");
            }

            int rowBytes = rect.Width * BytesPerPixel;
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                int source = ((y - frameBounds.Top) * frameWidth + (rect.Left - frameBounds.Left)) * BytesPerPixel;
                int target = ((y - monitorBounds.Top) * monitorBounds.Width + (rect.Left - monitorBounds.Left)) * BytesPerPixel;
                Buffer.BlockCopy(frame, source, canvas, target, rowBytes);
            }
        }
    }
}
