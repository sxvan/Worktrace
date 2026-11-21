using System.Numerics;

namespace Capture.Core.Pipeline;

/// <summary>64-bit difference hash of a BGRA image, computed on a 9x8 grayscale reduction.</summary>
public static class DHash
{
    private const int Columns = 9;
    private const int Rows = 8;

    public static ulong Compute(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer does not match the image dimensions.");
        }

        Span<long> cells = stackalloc long[Columns * Rows];
        for (int row = 0; row < Rows; row++)
        {
            int y0 = row * height / Rows, y1 = Math.Max(y0 + 1, (row + 1) * height / Rows);
            for (int column = 0; column < Columns; column++)
            {
                int x0 = column * width / Columns, x1 = Math.Max(x0 + 1, (column + 1) * width / Columns);

                // Sampling every few pixels is plenty for an average over a large cell.
                int stepX = Math.Max(1, (x1 - x0) / 48), stepY = Math.Max(1, (y1 - y0) / 48);
                long sum = 0;
                int count = 0;
                for (int y = y0; y < y1 && y < height; y += stepY)
                {
                    int offset = (y * width + x0) * 4;
                    for (int x = x0; x < x1 && x < width; x += stepX, offset += stepX * 4)
                    {
                        sum += (bgra[offset] * 29 + bgra[offset + 1] * 150 + bgra[offset + 2] * 77) >> 8;
                        count++;
                    }
                }
                cells[row * Columns + column] = count == 0 ? 0 : sum / count;
            }
        }

        ulong hash = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns - 1; column++)
            {
                hash <<= 1;
                if (cells[row * Columns + column] > cells[row * Columns + column + 1])
                {
                    hash |= 1;
                }
            }
        }
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>
/// Remembers the last saved image per window and tells whether a new one is close enough to it
/// to be skipped. An image is only compared with one saved on the same day, of the same size and
/// with the same window title: a changed title means changed content (another tab, another file),
/// which the coarse hash cannot see.
/// </summary>
public sealed class Deduplicator(int maxDistance)
{
    private sealed record Saved(long ImageId, string? Title, ulong Hash, int Width, int Height, DateOnly Day);

    private readonly Dictionary<long, Saved> _lastSaved = [];

    /// <summary>Returns the id of the earlier image this one duplicates, or null if it should be saved.</summary>
    public long? FindDuplicate(long windowHandle, string? title, int width, int height, ulong hash, DateOnly day)
    {
        if (_lastSaved.TryGetValue(windowHandle, out Saved? saved)
            && string.Equals(saved.Title, title, StringComparison.Ordinal)
            && saved.Day == day && saved.Width == width && saved.Height == height
            && DHash.HammingDistance(saved.Hash, hash) <= maxDistance)
        {
            return saved.ImageId;
        }
        return null;
    }

    public void Remember(long windowHandle, string? title, int width, int height, ulong hash, DateOnly day, long imageId)
    {
        // Entries from earlier days can never match again.
        foreach (long stale in _lastSaved.Where(e => e.Value.Day != day).Select(e => e.Key).ToList())
        {
            _lastSaved.Remove(stale);
        }
        _lastSaved[windowHandle] = new Saved(imageId, title, hash, width, height, day);
    }
}
