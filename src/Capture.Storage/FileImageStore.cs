using System.Globalization;
using Capture.Core.Pipeline;
using ImageMagick;

namespace Capture.Storage;

/// <summary>
/// Encodes redacted window images as AVIF and writes them to
/// captures\yyyy-MM-dd\HHmmss_fff_wN_Process.avif (local time) under the storage root.
/// This is the only place image data is written to disk.
/// </summary>
public sealed class FileImageStore(string root, int quality, int encoderSpeed, int maxDimension) : IImageStore
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
            $"{local.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_w{sequence}_{process}.avif");
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

        // Scale down so the longer side is at most maxDimension. Scale() averages pixels, which is
        // the cheapest method and keeps text clean. Never scales up.
        int longerSide = Math.Max(width, height);
        if (maxDimension > 0 && longerSide > maxDimension)
        {
            width = Math.Max(1, (int)Math.Round(width * (double)maxDimension / longerSide));
            height = Math.Max(1, (int)Math.Round(height * (double)maxDimension / longerSide));
            magick.Scale(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        }

        magick.Format = MagickFormat.Avif;
        magick.Quality = (uint)quality;
        // Encoder effort, 0 (smallest file) to 9 (fastest). On a 2560x1392 screenshot, 8 needs about
        // a quarter of the CPU time of the default for a file roughly a quarter larger.
        magick.Settings.SetDefine(MagickFormat.Heic, "speed", encoderSpeed.ToString(CultureInfo.InvariantCulture));
        return magick.ToByteArray();
    }

    private static string SafeFileNamePart(string value)
    {
        char[] chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "window" : new string(chars, 0, Math.Min(chars.Length, 40));
    }
}
