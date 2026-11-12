using System.Globalization;
using Capture.Core.Pipeline;
using ImageMagick;

namespace Capture.Storage;

/// <summary>
/// Encodes redacted composites as AVIF and writes them to
/// captures\yyyy-MM-dd\HHmmss_fff_mN.avif (local time) under the storage root.
/// This is the only place image data is written to disk.
/// </summary>
public sealed class FileImageStore(string root, int quality) : IImageStore
{
    public Task<string> SaveAsync(RedactedComposite composite, CancellationToken cancellationToken) =>
        Task.Run(() => Save(composite), cancellationToken);

    private string Save(RedactedComposite composite)
    {
        DateTime local = composite.Timestamp.LocalDateTime;
        string relativePath = Path.Combine(
            "captures",
            local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"{local.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_m{composite.Monitor.Index}.avif");
        string fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        byte[] encoded = Encode(composite);

        // Write under a temporary name first so a crash never leaves a half-written image behind.
        string temporaryPath = fullPath + ".tmp";
        File.WriteAllBytes(temporaryPath, encoded);
        File.Move(temporaryPath, fullPath, overwrite: true);
        return relativePath;
    }

    private byte[] Encode(RedactedComposite composite)
    {
        var readSettings = new PixelReadSettings(
            (uint)composite.Width, (uint)composite.Height, StorageType.Char, PixelMapping.BGRA);

        using var image = new MagickImage();
        image.ReadPixels(composite.Pixels, readSettings);
        // The canvas is opaque; the alpha channel only carries leftovers from window capture.
        image.Alpha(AlphaAction.Off);
        image.Format = MagickFormat.Avif;
        image.Quality = (uint)quality;
        return image.ToByteArray();
    }
}
