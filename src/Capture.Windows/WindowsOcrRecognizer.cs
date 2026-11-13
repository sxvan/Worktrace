using System.Runtime.InteropServices.WindowsRuntime;
using Capture.Core.Geometry;
using Capture.Core.Redaction;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Capture.Windows;

/// <summary>
/// Text recognition with the OCR engine built into Windows. Runs locally; needs no download and
/// no network access.
/// </summary>
public sealed class WindowsOcrRecognizer : ITextRecognizer
{
    public const double DefaultScale = 2.0;

    private readonly OcrEngine _engine;
    private readonly double _scale;

    private WindowsOcrRecognizer(OcrEngine engine, double scale)
    {
        _engine = engine;
        _scale = scale;
    }

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// Returns null if Windows has no OCR language installed. English is preferred because secrets
    /// and code are Latin text; otherwise the first language of the user's profile is used.
    /// </summary>
    /// <param name="scale">
    /// Enlargement before recognition. The engine is built for document-sized text and misreads
    /// 9 to 11 point screen text at its original size.
    /// </param>
    public static WindowsOcrRecognizer? TryCreate(double scale = DefaultScale)
    {
        OcrEngine? engine = null;
        var english = new Language("en-US");
        if (OcrEngine.IsLanguageSupported(english))
        {
            engine = OcrEngine.TryCreateFromLanguage(english);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        return engine is null ? null : new WindowsOcrRecognizer(engine, Math.Clamp(scale, 1.0, 4.0));
    }

    public async Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        // Never enlarge beyond what the engine accepts.
        double scale = Math.Min(_scale, Math.Min(
            (double)OcrEngine.MaxImageDimension / width, (double)OcrEngine.MaxImageDimension / height));
        if (scale < 1.0)
        {
            throw new InvalidOperationException("Image is larger than the OCR engine accepts.");
        }

        byte[] gray = ToEnlargedGray(bgra, width, height, scale, out int scaledWidth, out int scaledHeight);
        OcrResult result;
        try
        {
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                gray.AsBuffer(), BitmapPixelFormat.Gray8, scaledWidth, scaledHeight, BitmapAlphaMode.Ignore);
            result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        }
        finally
        {
            Array.Clear(gray);
        }

        var lines = new List<RecognizedLine>(result.Lines.Count);
        foreach (OcrLine line in result.Lines)
        {
            var words = new List<RecognizedWord>(line.Words.Count);
            foreach (OcrWord word in line.Words)
            {
                var r = word.BoundingRect;
                // Back to the coordinates of the original image.
                words.Add(new RecognizedWord(word.Text, new RectInt(
                    (int)Math.Floor(r.X / scale), (int)Math.Floor(r.Y / scale),
                    (int)Math.Ceiling((r.X + r.Width) / scale), (int)Math.Ceiling((r.Y + r.Height) / scale))));
            }
            lines.Add(new RecognizedLine(words));
        }
        return lines;
    }

    /// <summary>
    /// Grayscale copy of the image, enlarged with bilinear interpolation. Dark images are inverted,
    /// because the engine reads dark text on a light background more reliably.
    /// </summary>
    private static byte[] ToEnlargedGray(byte[] bgra, int width, int height, double scale, out int scaledWidth, out int scaledHeight)
    {
        byte[] source = new byte[width * height];
        long sum = 0;
        for (int i = 0, p = 0; i < source.Length; i++, p += 4)
        {
            byte value = (byte)((bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8);
            source[i] = value;
            sum += value;
        }
        if (sum / source.Length < 110)
        {
            for (int i = 0; i < source.Length; i++)
            {
                source[i] = (byte)(255 - source[i]);
            }
        }

        scaledWidth = (int)(width * scale);
        scaledHeight = (int)(height * scale);
        if (scaledWidth == width && scaledHeight == height)
        {
            return source;
        }

        byte[] target = new byte[scaledWidth * scaledHeight];
        try
        {
            // Fixed point with 8 fractional bits.
            int stepX = (int)((long)(width - 1) * 256 / Math.Max(1, scaledWidth - 1));
            int stepY = (int)((long)(height - 1) * 256 / Math.Max(1, scaledHeight - 1));
            for (int y = 0; y < scaledHeight; y++)
            {
                int sy = y * stepY, y0 = sy >> 8, fy = sy & 255, y1 = Math.Min(y0 + 1, height - 1);
                int row0 = y0 * width, row1 = y1 * width, targetRow = y * scaledWidth;
                for (int x = 0; x < scaledWidth; x++)
                {
                    int sx = x * stepX, x0 = sx >> 8, fx = sx & 255, x1 = Math.Min(x0 + 1, width - 1);
                    int top = source[row0 + x0] * (256 - fx) + source[row0 + x1] * fx;
                    int bottom = source[row1 + x0] * (256 - fx) + source[row1 + x1] * fx;
                    target[targetRow + x] = (byte)((top * (256 - fy) + bottom * fy) >> 16);
                }
            }
            return target;
        }
        finally
        {
            Array.Clear(source);
        }
    }
}
