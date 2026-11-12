namespace Capture.Core.Geometry;

/// <summary>Rectangle in physical pixels of the virtual screen. Right and Bottom are exclusive.</summary>
public readonly record struct RectInt(int Left, int Top, int Right, int Bottom)
{
    public static RectInt Empty => default;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static RectInt FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public RectInt Intersect(RectInt other)
    {
        var result = new RectInt(
            Math.Max(Left, other.Left), Math.Max(Top, other.Top),
            Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return result.IsEmpty ? Empty : result;
    }

    public bool Contains(RectInt other) =>
        !other.IsEmpty && other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

public static class Region
{
    /// <summary>
    /// The parts of <paramref name="source"/> not covered by any of <paramref name="covers"/>,
    /// as non-overlapping rectangles.
    /// </summary>
    public static List<RectInt> Subtract(RectInt source, IEnumerable<RectInt> covers)
    {
        var pieces = new List<RectInt>();
        if (source.IsEmpty)
        {
            return pieces;
        }
        pieces.Add(source);

        foreach (RectInt cover in covers)
        {
            if (pieces.Count == 0)
            {
                break;
            }
            if (cover.IsEmpty)
            {
                continue;
            }

            var next = new List<RectInt>(pieces.Count + 3);
            foreach (RectInt piece in pieces)
            {
                RectInt hit = piece.Intersect(cover);
                if (hit.IsEmpty)
                {
                    next.Add(piece);
                    continue;
                }

                // Full-width strips above and below the hit, then the left and right remainders beside it.
                if (hit.Top > piece.Top)
                {
                    next.Add(new RectInt(piece.Left, piece.Top, piece.Right, hit.Top));
                }
                if (hit.Bottom < piece.Bottom)
                {
                    next.Add(new RectInt(piece.Left, hit.Bottom, piece.Right, piece.Bottom));
                }
                if (hit.Left > piece.Left)
                {
                    next.Add(new RectInt(piece.Left, hit.Top, hit.Left, hit.Bottom));
                }
                if (hit.Right < piece.Right)
                {
                    next.Add(new RectInt(hit.Right, hit.Top, piece.Right, hit.Bottom));
                }
            }
            pieces = next;
        }

        return pieces;
    }
}
