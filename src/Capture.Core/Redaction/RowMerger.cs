using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <summary>
/// Text recognition often returns one visual row in pieces: "password" and "= hunter2" as two
/// separate lines. Detection needs the row as a whole, so words at the same height are put back
/// together, left to right.
/// </summary>
public static class RowMerger
{
    public static IReadOnlyList<RecognizedLine> MergeRows(IReadOnlyList<RecognizedLine> lines)
    {
        var rows = new List<(int Top, int Bottom, List<RecognizedWord> Words)>();

        foreach (RecognizedWord word in lines.SelectMany(l => l.Words).OrderBy(w => w.Bounds.Top))
        {
            if (word.Bounds.IsEmpty)
            {
                continue;
            }

            int match = -1;
            // Words arrive top to bottom, so only the most recent rows can still fit.
            for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 30; i--)
            {
                if (OverlapsEnough(rows[i].Top, rows[i].Bottom, word.Bounds))
                {
                    match = i;
                    break;
                }
            }

            if (match < 0)
            {
                rows.Add((word.Bounds.Top, word.Bounds.Bottom, [word]));
            }
            else
            {
                var row = rows[match];
                row.Words.Add(word);
                // The row keeps the height of its first word, so a tall neighbor cannot drag in other rows.
                rows[match] = row;
            }
        }

        return rows
            .Select(r => new RecognizedLine(r.Words.OrderBy(w => w.Bounds.Left).ToList()))
            .ToList();
    }

    /// <summary>Same row if the vertical overlap covers at least half of the smaller of the two heights.</summary>
    private static bool OverlapsEnough(int top, int bottom, RectInt word)
    {
        int overlap = Math.Min(bottom, word.Bottom) - Math.Max(top, word.Top);
        return overlap * 2 >= Math.Min(bottom - top, word.Height);
    }
}
