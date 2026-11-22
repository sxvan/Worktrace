namespace Capture.Core.Pipeline;

/// <summary>
/// The only way image data reaches the disk. Images are saved as captured, without text
/// recognition or redaction; the store encrypts them. Redaction happens on demand, whenever an
/// image or its text is taken out of storage.
/// </summary>
public interface IImageStore
{
    /// <summary>Encodes and saves the image. Returns its file path relative to the storage root.</summary>
    /// <param name="sequence">Position of the window within its capture, 1 = topmost. Part of the file name.</param>
    string Save(RawWindowImage image, int sequence);

    /// <summary>Removes a file written by <see cref="Save"/>. Missing files are ignored.</summary>
    void Delete(string relativePath);
}
