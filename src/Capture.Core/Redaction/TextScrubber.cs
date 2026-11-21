using System.Text;
using System.Text.RegularExpressions;
using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <summary>
/// Turns text into the form that may be stored as text: words the secret detectors flag become
/// "[redacted]". Optionally, URLs also lose their query string and fragment, where tokens and
/// session ids the detectors do not recognize can hide. Used for window titles (layer 1) and for
/// recognized screen text (layer 2).
/// </summary>
public sealed class TextScrubber(IReadOnlyList<ISecretDetector> detectors, bool stripUrlQueries = false)
{
    public const string RedactedMarker = "[redacted]";

    private const int CharWidth = 10;
    private const int LineHeight = 20;

    /// <summary>A URL with a scheme, or a bare host name directly followed by a path or query, that has a query or fragment.</summary>
    private static readonly Regex Url = new(
        @"(?:\b[A-Za-z][A-Za-z0-9+.\-]*://|\b(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}(?=[/?#]))[^\s?#]*[?#]\S+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static TextScrubber Default { get; } = new([new HeuristicSecretDetector()]);

    /// <summary>
    /// Scrubs plain text such as a window title. The text is laid out as one row of monospace
    /// words so the same detectors as for screen text can judge it. Null stays null.
    /// </summary>
    public string? Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var words = new List<RecognizedWord>();
        int column = 0;
        foreach (string word in text.Split(' '))
        {
            if (word.Length > 0)
            {
                words.Add(new RecognizedWord(word, new RectInt(
                    column * CharWidth, 0, (column + word.Length) * CharWidth, LineHeight)));
            }
            column += word.Length + 1;
        }

        IReadOnlyList<RecognizedLine> lines = [new RecognizedLine(words)];
        return Join(words, Hidden(words, FindSecrets(lines)));
    }

    /// <summary>
    /// Scrubs a row of recognized words, given the regions the detectors already found for the
    /// whole image. A word counts as hidden if a region covers at least half of it.
    /// </summary>
    public string ScrubWords(IReadOnlyList<RecognizedWord> words, IReadOnlyList<RectInt> secretRegions) =>
        Join(words, Hidden(words, secretRegions));

    public IReadOnlyList<RectInt> FindSecrets(IReadOnlyList<RecognizedLine> lines)
    {
        var regions = new List<RectInt>();
        foreach (ISecretDetector detector in detectors)
        {
            regions.AddRange(detector.FindSecrets(lines));
        }
        return regions;
    }

    /// <summary>Cuts the query string and fragment off every URL: https://host/path?code=... becomes https://host/path.</summary>
    public static string StripUrlQueries(string text)
    {
        try
        {
            return Url.Replace(text, match =>
            {
                int cut = match.Value.IndexOfAny(['?', '#']);
                return match.Value[..cut];
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactedMarker;
        }
    }

    private static bool[] Hidden(IReadOnlyList<RecognizedWord> words, IReadOnlyList<RectInt> regions)
    {
        var hidden = new bool[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            RectInt bounds = words[i].Bounds;
            long area = (long)bounds.Width * bounds.Height;
            foreach (RectInt region in regions)
            {
                RectInt overlap = region.Intersect(bounds);
                if (!overlap.IsEmpty && (long)overlap.Width * overlap.Height * 2 >= area)
                {
                    hidden[i] = true;
                    break;
                }
            }
        }
        return hidden;
    }

    /// <summary>Joins the words with spaces; each run of hidden words becomes one marker.</summary>
    private string Join(IReadOnlyList<RecognizedWord> words, bool[] hidden)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            if (hidden[i] && i > 0 && hidden[i - 1])
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(hidden[i] ? RedactedMarker : words[i].Text);
        }
        return stripUrlQueries ? StripUrlQueries(builder.ToString()) : builder.ToString();
    }
}
