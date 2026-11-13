using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <param name="Bounds">Position of the word in the image, in pixels.</param>
public sealed record RecognizedWord(string Text, RectInt Bounds);

/// <summary>One line of text as it appears on screen, words left to right.</summary>
public sealed record RecognizedLine(IReadOnlyList<RecognizedWord> Words);

/// <summary>
/// Reads the text in an image (OCR). Takes plain pixels and returns plain values, so an
/// implementation can run in a separate process.
/// </summary>
public interface ITextRecognizer
{
    /// <param name="bgra">32-bit BGRA pixels, rows top to bottom.</param>
    Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken);
}

/// <summary>
/// Finds text that must not be saved. Several detectors can run side by side: heuristics today,
/// a small model later.
/// </summary>
public interface ISecretDetector
{
    /// <summary>Returns the image regions to black out.</summary>
    IReadOnlyList<RectInt> FindSecrets(IReadOnlyList<RecognizedLine> lines);
}
