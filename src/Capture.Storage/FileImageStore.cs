using System.Globalization;
using Capture.Core.Pipeline;
using ImageMagick;

namespace Capture.Storage;

/// <summary>
/// Encodes redacted window images as lossless PNG and writes them to
/// captures\yyyy-MM-dd\HHmmss_fff_wN_Process.png (local time) under the storage root.
/// This is the only place image data is written to disk.
///
/// Lossless and at full size, because the images are read again later (text recognition, a
/// second redaction pass, a model): scaling down or lossy compression blurs small text until
/// characters can no longer be told apart. PNG is read by every tool and accepted by model APIs.
/// </summary>
public sealed class FileImageStore(string root, int maxDimension) : IImageStore
{
    public const string CapturesFolderName = "captures";
    public const string DayFolderFormat = "yyyy-MM-dd";

    public SavedImage Save(RedactedWindowImage image, int sequence)
    {
        DateTime local = image.Timestamp.LocalDateTime;
        string process = SafeFileNamePart(Path.GetFileNameWithoutExtension(image.Placement.Window.ProcessName ?? "window"));
        string relativePath = Path.Combine(
            CapturesFolderName,
            local.ToString(DayFolderFormat, CultureInfo.InvariantCulture),
            $"{local.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_w{sequence}_{process}.png");
        string fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        byte[] encoded = Encode(image, out int width, out int height);

        // Write under a temporary name first so a crash never leaves a half-written image behind.
        string temporaryPath = fullPath + ".tmp";
        File.WriteAllBytes(temporaryPath, encoded);
        File.Move(temporaryPath, fullPath, overwrite: true);
        return new SavedImage(relativePath, width, height);
    }

    public void Delete(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        // Only ever delete inside the captures folder.
        string capturesRoot = Path.GetFullPath(Path.Combine(root, CapturesFolderName)) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(capturesRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private byte[] Encode(RedactedWindowImage image, out int width, out int height)
    {
        width = image.Width;
        height = image.Height;
        var readSettings = new PixelReadSettings(
            (uint)image.Width, (uint)image.Height, StorageType.Char, PixelMapping.BGRA);

        using var magick = new MagickImage();
        magick.ReadPixels(image.Pixels, readSettings);
        // The image is opaque; the alpha channel only carries leftovers from window capture.
        magick.Alpha(AlphaAction.Off);

        // Optionally scale down so the longer side is at most maxDimension (0: keep the original
        // size). Scale() averages pixels, the cheapest method. Never scales up.
        int longerSide = Math.Max(width, height);
        if (maxDimension > 0 && longerSide > maxDimension)
        {
            width = Math.Max(1, (int)Math.Round(width * (double)maxDimension / longerSide));
            height = Math.Max(1, (int)Math.Round(height * (double)maxDimension / longerSide));
            magick.Scale(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        }

        // No metadata chunks (creation times and the like): the database holds the facts.
        magick.Strip();
        magick.Format = MagickFormat.Png;
        return magick.ToByteArray();
    }

    private static string SafeFileNamePart(string value)
    {
        char[] chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "window" : new string(chars, 0, Math.Min(chars.Length, 40));
    }
}
