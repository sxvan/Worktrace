using Capture.Core.Geometry;

namespace Capture.Core.Capturing;

/// <summary>Copies the visible parts of a captured window into its image. All buffers are 32-bit BGRA, rows top to bottom, no padding.</summary>
public static class WindowPixels
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Copies the given visible rectangles of a window frame onto a canvas; the rest of the canvas stays black.
    /// Throws if any rectangle lies outside the frame or the canvas: a caller that sees an
    /// exception must discard the whole capture.
    /// </summary>
    /// <param name="canvas">Target buffer, <paramref name="canvasBounds"/> in size.</param>
    /// <param name="canvasBounds">Virtual-screen rectangle the canvas stands for, normally the window bounds.</param>
    /// <param name="frame">Window frame pixels.</param>
    /// <param name="frameOrigin">Virtual-screen position of the frame's top-left pixel.</param>
    /// <param name="visibleRects">Virtual-screen rectangles to copy.</param>
    public static void CopyVisible(
        byte[] canvas,
        RectInt canvasBounds,
        byte[] frame,
        int frameWidth,
        int frameHeight,
        (int X, int Y) frameOrigin,
        IReadOnlyList<RectInt> visibleRects)
    {
        if (canvas.Length != (long)canvasBounds.Width * canvasBounds.Height * BytesPerPixel)
        {
            throw new ArgumentException("Canvas size does not match the canvas bounds.", nameof(canvas));
        }
        if (frame.Length != (long)frameWidth * frameHeight * BytesPerPixel)
        {
            throw new ArgumentException("Frame buffer size does not match the frame dimensions.", nameof(frame));
        }

        var frameBounds = RectInt.FromSize(frameOrigin.X, frameOrigin.Y, frameWidth, frameHeight);

        foreach (RectInt rect in visibleRects)
        {
            if (!canvasBounds.Contains(rect) || !frameBounds.Contains(rect))
            {
                throw new InvalidOperationException("Visible rectangle lies outside the canvas or the window frame.");
            }

            int rowBytes = rect.Width * BytesPerPixel;
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                int source = ((y - frameBounds.Top) * frameWidth + (rect.Left - frameBounds.Left)) * BytesPerPixel;
                int target = ((y - canvasBounds.Top) * canvasBounds.Width + (rect.Left - canvasBounds.Left)) * BytesPerPixel;
                Buffer.BlockCopy(frame, source, canvas, target, rowBytes);
            }
        }
    }
}
