namespace Capture.Core.Pipeline;

/// <param name="RelativePath">File path relative to the storage root.</param>
/// <param name="Width">Width of the saved file in pixels, which can be smaller than the window.</param>
public sealed record SavedImage(string RelativePath, int Width, int Height);

/// <summary>
/// The only way image data reaches the disk. Images are saved as captured, without text
/// recognition or redaction; the store encrypts them. Redaction happens on demand, whenever an
/// image or its text is taken out of storage.
/// </summary>
public interface IImageStore
{
    /// <summary>Encodes and saves the image, possibly scaled down.</summary>
    /// <param name="sequence">Position of the window within its capture, 1 = topmost. Part of the file name.</param>
    SavedImage Save(RawWindowImage image, int sequence);

    /// <summary>Removes a file written by <see cref="Save"/>. Missing files are ignored.</summary>
    void Delete(string relativePath);
}
