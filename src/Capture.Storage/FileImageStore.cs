using System.Globalization;
using System.Security.Cryptography;
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
///
/// With a <see cref="DayKeyStore"/>, every image is encrypted in memory before it is written
/// (see <see cref="ImageCipher"/>) and gets the extension .png.enc: no plaintext image ever
/// touches the disk, not even as a temporary file. <see cref="Read"/> decrypts again; it also
/// reads encrypted images when <paramref name="encrypt"/> is off, as long as the keys are there.
/// </summary>
public sealed class FileImageStore(string root, int maxDimension, DayKeyStore? keys = null, bool encrypt = true) : IImageStore
{
    private readonly bool _encrypt = keys is not null && encrypt;

    public const string CapturesFolderName = "captures";
    public const string DayFolderFormat = "yyyy-MM-dd";
    public const string EncryptedExtension = ".enc";

    public SavedImage Save(RedactedWindowImage image, int sequence)
    {
        DateTime local = image.Timestamp.LocalDateTime;
        string process = SafeFileNamePart(Path.GetFileNameWithoutExtension(image.Placement.Window.ProcessName ?? "window"));
        string relativePath = Path.Combine(
            CapturesFolderName,
            local.ToString(DayFolderFormat, CultureInfo.InvariantCulture),
            $"{local.ToString("HHmmss_fff", CultureInfo.InvariantCulture)}_w{sequence}_{process}.png"
            + (_encrypt ? EncryptedExtension : ""));

        byte[] encoded = Encode(image, out int width, out int height);
        Write(relativePath, DateOnly.FromDateTime(local), encoded);
        return new SavedImage(relativePath, width, height);
    }

    /// <summary>Writes an encoded image, encrypted if this store encrypts. Clears <paramref name="encoded"/> when it encrypts.</summary>
    internal void Write(string relativePath, DateOnly day, byte[] encoded)
    {
        string fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        byte[] data = encoded;
        if (_encrypt)
        {
            data = ImageCipher.Encrypt(keys!.GetOrCreate(day), encoded, relativePath);
            Array.Clear(encoded);
        }

        // Write under a temporary name first so a crash never leaves a half-written image behind.
        string temporaryPath = fullPath + ".tmp";
        File.WriteAllBytes(temporaryPath, data);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    /// <summary>
    /// The image file as an image again (PNG, or AVIF from earlier versions), decrypted if it is
    /// encrypted. Throws <see cref="CryptographicException"/> if the day's key is gone or the file
    /// was changed, moved or swapped.
    /// </summary>
    public byte[] Read(string relativePath)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(root, relativePath));
        if (!relativePath.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }
        if (keys is null)
        {
            throw new CryptographicException("No day keys available, so encrypted images cannot be read.");
        }
        DateOnly day = DayOf(relativePath)
                       ?? throw new CryptographicException("The image is not in a day folder, so its key is unknown.");
        byte[] key = keys.Find(day)
                     ?? throw new CryptographicException($"There is no key for {day:yyyy-MM-dd}; its images can no longer be decrypted.");
        try
        {
            return ImageCipher.Decrypt(key, file, relativePath);
        }
        finally
        {
            Array.Clear(key);
        }
    }

    /// <summary>The day folders that exist, newest first.</summary>
    public IReadOnlyList<DateOnly> Days()
    {
        string captures = Path.Combine(root, CapturesFolderName);
        if (!Directory.Exists(captures))
        {
            return [];
        }
        return Directory.GetDirectories(captures)
            .Select(d => DateOnly.TryParseExact(Path.GetFileName(d), DayFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) ? day : (DateOnly?)null)
            .OfType<DateOnly>()
            .OrderDescending()
            .ToList();
    }

    /// <summary>
    /// Writes the images of one day, decrypted, into <paramref name="targetFolder"/>. Returns how
    /// many images were written and how many could not be read.
    /// </summary>
    public (int Exported, int Failed) ExportDay(DateOnly day, string targetFolder)
    {
        string dayName = day.ToString(DayFolderFormat, CultureInfo.InvariantCulture);
        string folder = Path.Combine(root, CapturesFolderName, dayName);
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        Directory.CreateDirectory(targetFolder);
        int exported = 0, failed = 0;
        foreach (string file in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                byte[] image = Read(Path.Combine(CapturesFolderName, dayName, name));
                string targetName = name.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase)
                    ? name[..^EncryptedExtension.Length]
                    : name;
                File.WriteAllBytes(Path.Combine(targetFolder, targetName), image);
                exported++;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                failed++;
            }
        }
        return (exported, failed);
    }

    /// <summary>The day of a path "captures\yyyy-MM-dd\name", or null for any other path.</summary>
    internal static DateOnly? DayOf(string relativePath)
    {
        string[] parts = relativePath.Split(['\\', '/']);
        return parts.Length == 3 && DateOnly.TryParseExact(parts[1], DayFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
            ? day
            : null;
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
