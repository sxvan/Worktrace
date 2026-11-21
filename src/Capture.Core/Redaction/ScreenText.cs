using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <summary>
/// One piece of recognized text as it may be stored: secrets already replaced by
/// <see cref="TextScrubber.RedactedMarker"/>. The box is relative to the window image, 0..1,
/// so it stays meaningful when the image is scaled or not saved at all.
/// </summary>
public sealed record ScreenTextLine(string Text, double Left, double Top, double Right, double Bottom)
{
    public double Height => Bottom - Top;
}

public static class ScreenText
{
    /// <summary>
    /// A horizontal gap wider than this many line heights splits a row. Recognition merges a whole
    /// visual row across the window, so without the split a sidebar entry and the editor line next
    /// to it would read as one sentence.
    /// </summary>
    private const double SegmentGapInLineHeights = 2.5;

    /// <summary>
    /// Builds the storable text of an image from its merged rows and the secret regions found in
    /// them. Rows are split into segments at wide gaps; empty segments are left out.
    /// </summary>
    public static IReadOnlyList<ScreenTextLine> Build(
        IReadOnlyList<RecognizedLine> rows, IReadOnlyList<RectInt> secretRegions, int imageWidth, int imageHeight,
        TextScrubber scrubber)
    {
        var result = new List<ScreenTextLine>();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return result;
        }

        foreach (RecognizedLine row in rows)
        {
            foreach (List<RecognizedWord> segment in Segments(row.Words))
            {
                string text = scrubber.ScrubWords(segment, secretRegions).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                result.Add(new ScreenTextLine(
                    text,
                    Clamp((double)segment.Min(w => w.Bounds.Left) / imageWidth),
                    Clamp((double)segment.Min(w => w.Bounds.Top) / imageHeight),
                    Clamp((double)segment.Max(w => w.Bounds.Right) / imageWidth),
                    Clamp((double)segment.Max(w => w.Bounds.Bottom) / imageHeight)));
            }
        }
        return result;
    }

    internal static IEnumerable<List<RecognizedWord>> Segments(IReadOnlyList<RecognizedWord> words)
    {
        var current = new List<RecognizedWord>();
        foreach (RecognizedWord word in words)
        {
            if (current.Count > 0)
            {
                RecognizedWord previous = current[^1];
                int lineHeight = Math.Max(1, Math.Max(previous.Bounds.Height, word.Bounds.Height));
                if (word.Bounds.Left - previous.Bounds.Right > lineHeight * SegmentGapInLineHeights)
                {
                    yield return current;
                    current = [];
                }
            }
            current.Add(word);
        }
        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static double Clamp(double value) => Math.Round(Math.Clamp(value, 0, 1), 4);
}
