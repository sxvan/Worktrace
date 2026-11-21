using Capture.Core.Geometry;
using Capture.Core.Redaction;

namespace Capture.Core.Pipeline;

/// <summary>
/// Reads the text in a window image, asks the detectors what must not be saved, and blacks those
/// regions out. If the text cannot be read, the frame is dropped: an image that was not checked
/// is never saved.
///
/// The recognized text itself comes along with the image, scrubbed the same way
/// (see <see cref="ScreenText"/>), so it can be stored without the image.
/// </summary>
public sealed class TextRedactor(ITextRecognizer recognizer, IReadOnlyList<ISecretDetector> detectors) : IRedactor
{
    /// <summary>Extra pixels blacked out around each region, to cover glyph edges the recognizer cut off.</summary>
    private const int Padding = 3;

    public async ValueTask<RedactionResult> RedactAsync(RawWindowImage image, CancellationToken cancellationToken)
    {
        IReadOnlyList<RecognizedLine> lines;
        try
        {
            lines = RowMerger.MergeRows(
                await recognizer.RecognizeAsync(image.Pixels, image.Width, image.Height, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RedactionResult.Drop($"text recognition failed ({ex.GetType().Name} 0x{ex.HResult:X8})");
        }

        IReadOnlyList<RectInt> regions = new TextScrubber(detectors).FindSecrets(lines);
        IReadOnlyList<ScreenTextLine> text = ScreenText.Build(lines, regions, image.Width, image.Height);

        var bounds = new RectInt(0, 0, image.Width, image.Height);
        byte[] pixels = image.Pixels;
        foreach (RectInt region in regions)
        {
            RectInt padded = new RectInt(region.Left - Padding, region.Top - Padding, region.Right + Padding, region.Bottom + Padding)
                .Intersect(bounds);
            for (int y = padded.Top; y < padded.Bottom; y++)
            {
                Array.Clear(pixels, (y * image.Width + padded.Left) * 4, padded.Width * 4);
            }
        }

        return RedactionResult.Redacted(new RedactedWindowImage(
            image.Timestamp, image.Placement, image.Width, image.Height, image.DetachPixels(), regions.Count, text));
    }
}
