using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <param name="Bounds">Position of the word in the image, in pixels.</param>
public sealed record RecognizedWord(string Text, RectInt Bounds);

/// <summary>
/// One line of text, words left to right. Window titles are laid out this way (see
/// <see cref="TextScrubber.Scrub"/>) so the detectors judge them like text on screen.
/// </summary>
public sealed record RecognizedLine(IReadOnlyList<RecognizedWord> Words);

/// <summary>
/// Finds text that must not be saved. Several detectors can run side by side: heuristics today,
/// a small model later.
/// </summary>
public interface ISecretDetector
{
    /// <summary>Returns the image regions to black out.</summary>
    IReadOnlyList<RectInt> FindSecrets(IReadOnlyList<RecognizedLine> lines);
}

/// <summary>One line of text as a recognizer returns it: the text and its box in the image.</summary>
public sealed record OcrLine(string Text, RectInt Bounds, float Confidence);

/// <summary>
/// Reads the text in an image. Runs only on demand, on an image taken out of storage, never at
/// capture time; its result is redacted before anything else sees it (<see cref="ScreenTextRedactor"/>).
/// </summary>
public interface ITextRecognizer
{
    /// <param name="image">An encoded image (PNG).</param>
    IReadOnlyList<OcrLine> Recognize(byte[] image);
}
