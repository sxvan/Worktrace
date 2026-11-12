namespace Capture.Core.Pipeline;

/// <summary>Result of the redaction stage: either an image that may be saved, or "drop this frame".</summary>
public sealed class RedactionResult
{
    private RedactionResult(RedactedWindowImage? image, string? dropReason)
    {
        Image = image;
        DropReason = dropReason;
    }

    public RedactedWindowImage? Image { get; }

    /// <summary>Technical reason; never contains screen content.</summary>
    public string? DropReason { get; }

    public bool IsDropped => Image is null;

    internal static RedactionResult Redacted(RedactedWindowImage image) => new(image, null);

    public static RedactionResult Drop(string reason) => new(null, reason);
}

/// <summary>
/// Removes sensitive content from a window image before it can be saved. Takes ownership of the
/// raw image. Async, and working only on plain pixel and metadata values, so a later
/// implementation can run OCR and secret detection in a separate process.
///
/// Later milestone: the password-field check (UI Automation IsPassword on the focused element)
/// does not belong here. It runs before capture, in the capture step, so nothing is captured at all.
/// </summary>
public interface IRedactor
{
    ValueTask<RedactionResult> RedactAsync(RawWindowImage image, CancellationToken cancellationToken);
}

/// <summary>First implementation: returns its input unchanged.</summary>
public sealed class PassThroughRedactor : IRedactor
{
    public ValueTask<RedactionResult> RedactAsync(RawWindowImage image, CancellationToken cancellationToken)
    {
        var redacted = new RedactedWindowImage(
            image.Timestamp, image.Placement, image.Width, image.Height, image.DetachPixels());
        return ValueTask.FromResult(RedactionResult.Redacted(redacted));
    }
}

/// <param name="RelativePath">File path relative to the storage root.</param>
/// <param name="Width">Width of the saved file in pixels, which can be smaller than the window.</param>
public sealed record SavedImage(string RelativePath, int Width, int Height);

/// <summary>
/// The only way image data reaches the disk. Accepts nothing but redacted images.
/// Encryption at rest will be added behind this interface.
/// </summary>
public interface IImageStore
{
    /// <summary>Encodes and saves the image, possibly scaled down.</summary>
    /// <param name="sequence">Position of the window within its capture, 1 = topmost. Part of the file name.</param>
    SavedImage Save(RedactedWindowImage image, int sequence);

    /// <summary>Removes a file written by <see cref="Save"/>. Missing files are ignored.</summary>
    void Delete(string relativePath);
}
