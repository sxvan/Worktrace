using Capture.Core.Geometry;
using Capture.Core.Redaction;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace Capture.Cli;

/// <summary>
/// PaddleOCR (PP-OCRv5, Latin script: English, German and the other Latin-alphabet languages),
/// running in this process on the CPU. The models ship with the program; nothing is downloaded and
/// no image leaves the machine here.
/// </summary>
internal sealed class PaddleTextRecognizer : ITextRecognizer, IDisposable
{
    /// <summary>
    /// Longest image side the detector works on. Screenshots are kept at full size so small text
    /// stays readable; the default (960) would shrink a full-HD window by half.
    /// </summary>
    private const int DetectorMaxSize = 4096;

    private readonly PaddleOcrAll _ocr;

    public PaddleTextRecognizer()
    {
        // Paddle's native logging (glog) writes info and deprecation warnings to stderr, which
        // Claude reads. Errors only; this must be set before the native library is first used.
        Environment.SetEnvironmentVariable("GLOG_minloglevel", "2");
        _ocr = new PaddleOcrAll(LocalFullModels.LatinV5, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
        _ocr.Detector.MaxSize = DetectorMaxSize;
    }

    public IReadOnlyList<OcrLine> Recognize(byte[] image)
    {
        using Mat mat = Cv2.ImDecode(image, ImreadModes.Color);
        if (mat.Empty())
        {
            throw new InvalidDataException("The screenshot could not be decoded.");
        }

        PaddleOcrResult result = _ocr.Run(mat);
        var lines = new List<OcrLine>(result.Regions.Length);
        foreach (PaddleOcrResultRegion region in result.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Text))
            {
                continue;
            }
            Rect box = region.Rect.BoundingRect();
            var bounds = new RectInt(
                Math.Max(0, box.Left), Math.Max(0, box.Top), Math.Min(mat.Width, box.Right), Math.Min(mat.Height, box.Bottom));
            if (!bounds.IsEmpty)
            {
                lines.Add(new OcrLine(region.Text, bounds, region.Score));
            }
        }

        // In the detector's order; ScreenTextRedactor puts the pieces into rows.
        return lines;
    }

    public void Dispose() => _ocr.Dispose();
}
