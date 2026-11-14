using Capture.Core.Capturing;
using Capture.Core.Geometry;

namespace Capture.Core.Tests;

public class WindowPixelsTests
{
    /// <summary>A frame where every pixel's four bytes hold the given value.</summary>
    private static byte[] Solid(int width, int height, byte value)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static byte PixelAt(byte[] canvas, RectInt monitor, int x, int y) =>
        canvas[((y - monitor.Top) * monitor.Width + (x - monitor.Left)) * 4];

    [Fact]
    public void Only_the_visible_rectangles_are_drawn_and_the_rest_stays_black()
    {
        // Monitor left of the primary, so all coordinates are negative.
        var monitor = new RectInt(-100, -50, 0, 0);
        byte[] canvas = new byte[100 * 50 * 4];
        var windowBounds = new RectInt(-80, -40, -20, -10);
        byte[] frame = Solid(60, 30, 200);
        RectInt[] visible = [new(-80, -40, -50, -10)];

        WindowPixels.CopyVisible(canvas, monitor, frame, 60, 30, (windowBounds.Left, windowBounds.Top), visible);

        for (int x = monitor.Left; x < monitor.Right; x++)
        {
            for (int y = monitor.Top; y < monitor.Bottom; y++)
            {
                bool inside = visible[0].Contains(new RectInt(x, y, x + 1, y + 1));
                Assert.Equal(inside ? 200 : 0, PixelAt(canvas, monitor, x, y));
            }
        }
    }

    [Fact]
    public void Pixels_land_at_the_matching_position()
    {
        var monitor = new RectInt(0, 0, 8, 8);
        byte[] canvas = new byte[8 * 8 * 4];
        // 4x4 frame at (2,3); each pixel's value encodes its own frame position.
        byte[] frame = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Array.Fill(frame, (byte)(10 * y + x + 1), (y * 4 + x) * 4, 4);
            }
        }

        WindowPixels.CopyVisible(canvas, monitor, frame, 4, 4, (2, 3), [new RectInt(3, 4, 6, 7)]);

        Assert.Equal(10 * 1 + 1 + 1, PixelAt(canvas, monitor, 3, 4)); // frame pixel (1,1)
        Assert.Equal(10 * 3 + 3 + 1, PixelAt(canvas, monitor, 5, 6)); // frame pixel (3,3)
        Assert.Equal(0, PixelAt(canvas, monitor, 2, 3));              // inside the window but not visible
    }

    [Fact]
    public void Rectangle_outside_the_window_frame_is_rejected()
    {
        byte[] canvas = new byte[10 * 10 * 4];

        Assert.Throws<InvalidOperationException>(() => WindowPixels.CopyVisible(
            canvas, new RectInt(0, 0, 10, 10), Solid(4, 4, 1), 4, 4, (0, 0), [new RectInt(2, 2, 6, 4)]));
    }

    [Fact]
    public void Rectangle_outside_the_monitor_is_rejected()
    {
        byte[] canvas = new byte[10 * 10 * 4];

        Assert.Throws<InvalidOperationException>(() => WindowPixels.CopyVisible(
            canvas, new RectInt(0, 0, 10, 10), Solid(20, 4, 1), 20, 4, (0, 0), [new RectInt(5, 0, 15, 4)]));
    }

    [Fact]
    public void Buffer_size_mismatch_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WindowPixels.CopyVisible(
            new byte[16], new RectInt(0, 0, 10, 10), Solid(4, 4, 1), 4, 4, (0, 0), []));
        Assert.Throws<ArgumentException>(() => WindowPixels.CopyVisible(
            new byte[10 * 10 * 4], new RectInt(0, 0, 10, 10), new byte[8], 4, 4, (0, 0), []));
    }
}
