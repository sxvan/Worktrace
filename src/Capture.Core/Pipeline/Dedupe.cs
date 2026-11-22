namespace Capture.Core.Pipeline;

/// <summary>
/// Remembers the last saved image of each window and tells whether a new one shows the same, so
/// it need not be saved again. Compared at full resolution: on screen, what changes is mostly
/// text, and a coarse perceptual hash cannot see text. An image is only compared
/// with one saved on the same day, of the same size and with the same window title.
///
/// The images are compared in grayscale, in tiles of <see cref="TileSize"/> pixels. A tile has
/// changed when enough of its pixels changed noticeably, spread wider than a text cursor; one
/// changed tile is enough for the image to be saved. A blinking cursor, slight color noise and
/// anti-aliasing do not count.
///
/// Holds one grayscale copy (1 byte per pixel) per window in memory, cleared when replaced.
/// </summary>
public sealed class Deduplicator
{
    public const int TileSize = 32;

    /// <summary>A pixel has changed when its gray value differs by more than this (0..255).</summary>
    public const int PixelTolerance = 24;

    /// <summary>A tile has changed when at least this many of its pixels changed...</summary>
    public const int MinChangedPixelsPerTile = 12;

    /// <summary>...spread over at least this many columns: a text cursor is 1 to 3 pixels wide.</summary>
    public const int MinChangedWidth = 4;

    private sealed record Saved(long ImageId, string? Title, int Width, int Height, DateOnly Day, byte[] Gray);

    private readonly Dictionary<long, Saved> _lastSaved = [];

    /// <summary>The image as one gray byte per pixel, for <see cref="FindDuplicate"/> and <see cref="Remember"/>.</summary>
    public static byte[] Grayscale(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer does not match the image dimensions.");
        }
        byte[] gray = new byte[width * height];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 4)
        {
            gray[i] = (byte)((bgra[o] * 29 + bgra[o + 1] * 150 + bgra[o + 2] * 77) >> 8);
        }
        return gray;
    }

    /// <summary>Returns the id of the earlier image this one duplicates, or null if it should be saved.</summary>
    public long? FindDuplicate(long windowHandle, string? title, int width, int height, byte[] gray, DateOnly day)
    {
        if (_lastSaved.TryGetValue(windowHandle, out Saved? saved)
            && string.Equals(saved.Title, title, StringComparison.Ordinal)
            && saved.Day == day && saved.Width == width && saved.Height == height
            && !HasChanged(saved.Gray, gray, width, height))
        {
            return saved.ImageId;
        }
        return null;
    }

    /// <summary>Remembers a saved image. Takes ownership of <paramref name="gray"/>.</summary>
    public void Remember(long windowHandle, string? title, int width, int height, byte[] gray, DateOnly day, long imageId)
    {
        // Entries from earlier days can never match again.
        foreach (long stale in _lastSaved.Where(e => e.Value.Day != day).Select(e => e.Key).ToList())
        {
            Array.Clear(_lastSaved[stale].Gray);
            _lastSaved.Remove(stale);
        }
        if (_lastSaved.TryGetValue(windowHandle, out Saved? previous) && !ReferenceEquals(previous.Gray, gray))
        {
            Array.Clear(previous.Gray);
        }
        _lastSaved[windowHandle] = new Saved(imageId, title, width, height, day, gray);
    }

    /// <summary>Forgets every remembered image, e.g. after their rows were deleted.</summary>
    public void Clear()
    {
        foreach (Saved saved in _lastSaved.Values)
        {
            Array.Clear(saved.Gray);
        }
        _lastSaved.Clear();
    }

    /// <summary>True if any tile of the two images (same size, grayscale) changed noticeably.</summary>
    public static bool HasChanged(byte[] before, byte[] after, int width, int height)
    {
        for (int tileTop = 0; tileTop < height; tileTop += TileSize)
        {
            int tileBottom = Math.Min(height, tileTop + TileSize);
            for (int tileLeft = 0; tileLeft < width; tileLeft += TileSize)
            {
                int tileRight = Math.Min(width, tileLeft + TileSize);
                int changed = 0, minX = int.MaxValue, maxX = -1;
                for (int y = tileTop; y < tileBottom; y++)
                {
                    int row = y * width;
                    for (int x = tileLeft; x < tileRight; x++)
                    {
                        if (Math.Abs(before[row + x] - after[row + x]) > PixelTolerance)
                        {
                            changed++;
                            if (x < minX)
                            {
                                minX = x;
                            }
                            if (x > maxX)
                            {
                                maxX = x;
                            }
                        }
                    }
                }
                if (changed >= MinChangedPixelsPerTile && maxX - minX + 1 >= MinChangedWidth)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
