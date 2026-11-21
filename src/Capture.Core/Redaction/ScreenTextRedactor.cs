using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <summary>One row of recognized text after redaction: hidden words are replaced by the marker.</summary>
/// <param name="Bounds">The row's box in the image, in pixels.</param>
public sealed record RedactedLine(string Text, RectInt Bounds);

/// <summary>What may leave storage of one screenshot: its text after redaction, and the image regions to black out.</summary>
public sealed record RedactedScreenText(IReadOnlyList<RedactedLine> Lines, IReadOnlyList<RectInt> HiddenRegions);

/// <summary>
/// Applies the redaction rules to text recognized in a screenshot.
///
/// Recognizers return pieces of text (PaddleOCR often splits a row where the spacing is wide), so
/// the pieces are first put together into rows, top to bottom and left to right: a rule such as
/// "password: value" only works when the name and the value are judged together. Each piece is
/// split into words with boxes estimated from the character positions; the secret detectors judge
/// those words like a window title, and every hidden word is both replaced in the text and blacked
/// out in the image. The estimated boxes are widened, since characters are not all the same width:
/// blacking out a little too much costs nothing.
/// </summary>
public sealed class ScreenTextRedactor(TextScrubber scrubber)
{
    public RedactedScreenText Redact(IReadOnlyList<OcrLine> ocr)
    {
        IReadOnlyList<IReadOnlyList<OcrLine>> rows = Rows(ocr);
        var lines = rows.Select(row => new RecognizedLine(row.SelectMany(Words).ToList())).ToList();

        IReadOnlyList<RectInt> secrets = scrubber.FindSecrets(lines);
        IReadOnlyList<string> texts = scrubber.ScrubLines(lines, secrets);

        var hidden = new List<RectInt>(secrets.Count);
        foreach (RectInt region in secrets.Distinct())
        {
            // About one character of margin on each side, a little above and below.
            int margin = Math.Max(2, region.Height / 2);
            hidden.Add(new RectInt(region.Left - margin, region.Top - 2, region.Right + margin, region.Bottom + 2));
        }

        var redacted = new List<RedactedLine>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            redacted.Add(new RedactedLine(texts[i], Union(rows[i].Select(p => p.Bounds))));
        }
        return new RedactedScreenText(redacted, hidden);
    }

    /// <summary>
    /// Groups pieces into lines: a piece joins a row when their vertical extents overlap by at least
    /// half the smaller height, and a row is split where a wide gap separates two columns. Lines
    /// top to bottom, and left to right within a row; pieces in a line left to right.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<OcrLine>> Rows(IReadOnlyList<OcrLine> pieces)
    {
        var rows = new List<(int Top, int Bottom, List<OcrLine> Pieces)>();
        foreach (OcrLine piece in pieces.Where(p => !p.Bounds.IsEmpty).OrderBy(p => p.Bounds.Top + p.Bounds.Bottom))
        {
            int index = rows.FindIndex(row =>
            {
                int overlap = Math.Min(row.Bottom, piece.Bounds.Bottom) - Math.Max(row.Top, piece.Bounds.Top);
                return overlap * 2 >= Math.Min(row.Bottom - row.Top, piece.Bounds.Height);
            });
            if (index < 0)
            {
                rows.Add((piece.Bounds.Top, piece.Bounds.Bottom, [piece]));
            }
            else
            {
                (int top, int bottom, List<OcrLine> list) = rows[index];
                list.Add(piece);
                rows[index] = (Math.Min(top, piece.Bounds.Top), Math.Max(bottom, piece.Bounds.Bottom), list);
            }
        }
        var lines = new List<IReadOnlyList<OcrLine>>();
        foreach (var row in rows.OrderBy(r => r.Top + r.Bottom))
        {
            // Pieces side by side in different columns (a sidebar and the main text) share a row
            // but not a line: a wide gap starts a new one.
            List<OcrLine> line = [];
            foreach (OcrLine piece in row.Pieces.OrderBy(p => p.Bounds.Left))
            {
                if (line.Count > 0 && piece.Bounds.Left - line[^1].Bounds.Right > ColumnGap(line[^1], piece))
                {
                    lines.Add(line);
                    line = [];
                }
                line.Add(piece);
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>A horizontal gap wider than this many line heights separates two columns.</summary>
    internal const int ColumnGapInLineHeights = 4;

    private static int ColumnGap(OcrLine left, OcrLine right) =>
        ColumnGapInLineHeights * Math.Max(left.Bounds.Height, right.Bounds.Height);

    /// <summary>The words of a piece, with boxes spread over it in proportion to their characters.</summary>
    internal static IReadOnlyList<RecognizedWord> Words(OcrLine line)
    {
        string text = line.Text;
        var words = new List<RecognizedWord>();
        if (text.Length == 0 || line.Bounds.IsEmpty)
        {
            return words;
        }

        double perChar = (double)line.Bounds.Width / text.Length;
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            int left = line.Bounds.Left + (int)Math.Floor(start * perChar);
            int right = line.Bounds.Left + (int)Math.Ceiling(i * perChar);
            words.Add(new RecognizedWord(text[start..i], new RectInt(left, line.Bounds.Top, Math.Max(right, left + 1), line.Bounds.Bottom)));
        }
        return words;
    }

    private static RectInt Union(IEnumerable<RectInt> boxes)
    {
        List<RectInt> list = boxes.ToList();
        return new RectInt(list.Min(b => b.Left), list.Min(b => b.Top), list.Max(b => b.Right), list.Max(b => b.Bottom));
    }
}
