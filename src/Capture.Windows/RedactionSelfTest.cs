using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Capture.Core.Geometry;
using Capture.Core.Redaction;

namespace Capture.Windows;

/// <summary>
/// Checks secret redaction on this machine without touching the screen: draws lines of fake code
/// containing made-up secrets the way an editor or terminal would show them, runs the real
/// recognizer and detectors on that picture, and reports which secrets were found.
/// </summary>
public static class RedactionSelfTest
{
    public sealed record Case(string Theme, float FontSize, string Line, string Secret, bool Found, string RecognizedAs);

    public sealed record Report(IReadOnlyList<Case> Cases, int FalsePositives, long MillisecondsPerImage, int Width, int Height);

    public static async Task<Report> RunAsync(ITextRecognizer recognizer, IReadOnlyList<ISecretDetector> detectors)
    {
        // Assembled at run time; none of these is a real credential.
        string body = string.Concat(Enumerable.Repeat("a1B2c3D4e5", 4));
        (string Line, string Secret)[] secretLines =
        [
            ("export ANTHROPIC_API_KEY=sk-" + "ant-api03-" + body, "sk-" + "ant-api03-" + body),
            ("git remote set-url origin https://gh" + "p_" + body + "@github.com/acme/app.git", "gh" + "p_" + body),
            ("aws_access_key_id = AKIA" + "IOSFODNN7EXAMPLE", "AKIA" + "IOSFODNN7EXAMPLE"),
            ("password = hunter2", "hunter2"),
            ("\"client_secret\": \"Zx9Kq2Lm8Pw4Tr6Y\",", "Zx9Kq2Lm8Pw4Tr6Y"),
            ("DATABASE_URL=postgres://admin:s3cretpw@db.internal:5432/app", "s3cretpw"),
            ("Authorization: Bearer abcdefghijklmnop1234567890QRSTUV", "abcdefghijklmnop1234567890QRSTUV"),
            ("-----BEGIN OPENSSH PRIVATE KEY-----", "PRIVATE KEY"),
            ("-----END OPENSSH PRIVATE KEY-----", "PRIVATE KEY"),
        ];
        string[] harmlessLines =
        [
            "public async Task<int> CountTokensAsync(string text, CancellationToken token)",
            "    var result = await client.SendAsync(request, cancellationToken);",
            "commit 3f786850e387550fdab836ed7e6dc881de23001b (HEAD -> main)",
            "The quick brown fox jumps over the lazy dog 0123456789.",
            "maxTokens: number; usePassword: boolean; // settings",
        ];

        var cases = new List<Case>();
        int falsePositives = 0;
        long totalMs = 0;
        int images = 0, width = 0, height = 0;

        foreach ((string theme, Color background, Color foreground) in new[]
                 {
                     ("dark", Color.FromArgb(30, 30, 30), Color.FromArgb(212, 212, 212)),
                     ("light", Color.White, Color.FromArgb(30, 30, 30)),
                 })
        {
            foreach (float fontSize in new[] { 10.5f, 9f })
            {
                (byte[] pixels, width, height, List<RectInt> lineBoxes) = Render(
                    secretLines.Select(s => s.Line).Concat(harmlessLines).ToList(), background, foreground, fontSize);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    IReadOnlyList<RecognizedLine> lines = RowMerger.MergeRows(
                        await recognizer.RecognizeAsync(pixels, width, height, CancellationToken.None));
                    var regions = detectors.SelectMany(d => d.FindSecrets(lines)).ToList();
                    totalMs += stopwatch.ElapsedMilliseconds;
                    images++;

                    for (int i = 0; i < lineBoxes.Count; i++)
                    {
                        bool hit = regions.Any(r => !r.Intersect(lineBoxes[i]).IsEmpty);
                        if (i < secretLines.Length)
                        {
                            string recognized = string.Join(" ", lines
                                .Where(l => l.Words.Any(w => !w.Bounds.Intersect(lineBoxes[i]).IsEmpty))
                                .SelectMany(l => l.Words).Select(w => w.Text));
                            cases.Add(new Case(theme, fontSize, secretLines[i].Line, secretLines[i].Secret, hit, recognized));
                        }
                        else if (hit)
                        {
                            falsePositives++;
                        }
                    }
                }
                finally
                {
                    Array.Clear(pixels);
                }
            }
        }

        return new Report(cases, falsePositives, images == 0 ? 0 : totalMs / images, width, height);
    }

    /// <summary>Draws the lines in Consolas on a 1600x900 canvas and returns BGRA pixels plus each line's box.</summary>
    private static (byte[] Pixels, int Width, int Height, List<RectInt> LineBoxes) Render(
        IReadOnlyList<string> lines, Color background, Color foreground, float fontSize)
    {
        const int width = 1600, height = 900;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(foreground);

        graphics.Clear(background);
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int lineHeight = (int)Math.Ceiling(font.GetHeight(graphics)) + 4;
        var boxes = new List<RectInt>();
        for (int i = 0; i < lines.Count; i++)
        {
            int y = 20 + i * lineHeight * 2; // blank line between entries so lines never merge
            graphics.DrawString(lines[i], font, brush, 40, y);
            boxes.Add(new RectInt(0, y - 2, width, y + lineHeight));
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
            return (pixels, width, height, boxes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
