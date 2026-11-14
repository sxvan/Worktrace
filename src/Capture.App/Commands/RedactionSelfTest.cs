using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Capture.Core.Geometry;
using Capture.Core.Redaction;

namespace Capture.App.Commands;

/// <summary>
/// Checks secret redaction on this machine without touching the screen: draws lines of fake code
/// containing made-up secrets the way an editor or terminal would show them, runs the real
/// recognizer and detectors on that picture, and reports which secrets were found and whether
/// the black box would sit on top of them.
/// </summary>
public static class RedactionSelfTest
{
    /// <param name="Side">Where on the canvas the line was drawn: "left" or "right".</param>
    /// <param name="Found">A redaction region touches the secret's row.</param>
    /// <param name="Covered">The redaction regions cover the box around the secret's visible characters.</param>
    public sealed record Case(
        string Theme, float FontSize, string Side, string Line, string Secret, bool Found, bool Covered, string RecognizedAs);

    public sealed record Report(IReadOnlyList<Case> Cases, int FalsePositives, long MillisecondsPerImage, int Width, int Height);

    // The size of a maximized window on a 2560x1440 monitor.
    private const int CanvasWidth = 2560;
    private const int CanvasHeight = 1392;

    private sealed record DrawnLine(string Text, string? Secret, string Side, RectInt RowBox, RectInt SecretBox);

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
        int images = 0;

        foreach ((string theme, Color background, Color foreground) in new[]
                 {
                     ("dark", Color.FromArgb(30, 30, 30), Color.FromArgb(212, 212, 212)),
                     ("light", Color.White, Color.FromArgb(30, 30, 30)),
                 })
        {
            foreach (float fontSize in new[] { 10.5f, 9f })
            {
                (byte[] pixels, List<DrawnLine> drawn) = Render(secretLines, harmlessLines, background, foreground, fontSize);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    IReadOnlyList<RecognizedLine> lines = RowMerger.MergeRows(
                        await recognizer.RecognizeAsync(pixels, CanvasWidth, CanvasHeight, CancellationToken.None));
                    var regions = detectors.SelectMany(d => d.FindSecrets(lines)).ToList();
                    totalMs += stopwatch.ElapsedMilliseconds;
                    images++;

                    foreach (DrawnLine line in drawn)
                    {
                        bool hit = regions.Any(r => !r.Intersect(line.RowBox).IsEmpty);
                        if (line.Secret is null)
                        {
                            falsePositives += hit ? 1 : 0;
                            continue;
                        }

                        string recognized = string.Join(" ", lines
                            .SelectMany(l => l.Words)
                            .Where(w => !w.Bounds.Intersect(line.RowBox).IsEmpty)
                            .OrderBy(w => w.Bounds.Left)
                            .Select(w => w.Text));
                        cases.Add(new Case(theme, fontSize, line.Side, line.Text, line.Secret, hit,
                            hit && CoveredFraction(line.SecretBox, regions) >= 0.99, recognized));
                    }
                }
                finally
                {
                    Array.Clear(pixels);
                }
            }
        }

        return new Report(cases, falsePositives, images == 0 ? 0 : totalMs / images, CanvasWidth, CanvasHeight);
    }

    /// <summary>
    /// The smallest box around the pixels that differ from the background: where the characters
    /// really are, without the empty space a text layout box has above and below them.
    /// </summary>
    private static RectInt InkBounds(byte[] bgra, RectInt area, Color background)
    {
        if (area.IsEmpty)
        {
            return area;
        }
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int i = (y * CanvasWidth + x) * 4;
                int difference = Math.Abs(bgra[i] - background.B) + Math.Abs(bgra[i + 1] - background.G) + Math.Abs(bgra[i + 2] - background.R);
                if (difference > 120)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }
        return right < left ? RectInt.Empty : new RectInt(left, top, right, bottom);
    }

    /// <summary>Share of the box's pixels that lie inside at least one region.</summary>
    private static double CoveredFraction(RectInt box, List<RectInt> regions)
    {
        if (box.IsEmpty)
        {
            return 0;
        }
        long covered = 0;
        for (int x = box.Left; x < box.Right; x++)
        {
            for (int y = box.Top; y < box.Bottom; y++)
            {
                foreach (RectInt region in regions)
                {
                    if (x >= region.Left && x < region.Right && y >= region.Top && y < region.Bottom)
                    {
                        covered++;
                        break;
                    }
                }
            }
        }
        return (double)covered / ((long)box.Width * box.Height);
    }

    /// <summary>
    /// Draws every line twice in Consolas, once near the left edge and once starting far to the
    /// right, so position errors that grow across the image show up. Returns BGRA pixels and where
    /// each line and its secret ended up.
    /// </summary>
    private static (byte[] Pixels, List<DrawnLine> Lines) Render(
        (string Line, string Secret)[] secretLines, string[] harmlessLines, Color background, Color foreground, float fontSize)
    {
        using var bitmap = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(foreground);

        graphics.Clear(background);
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int lineHeight = (int)Math.Ceiling(font.GetHeight(graphics)) + 4;
        var drawn = new List<DrawnLine>();
        int row = 0;

        foreach ((string side, int x) in new[] { ("left", 40), ("right", 1500) })
        {
            var all = secretLines.Select(s => (s.Line, (string?)s.Secret)).Concat(harmlessLines.Select(h => (h, (string?)null)));
            foreach ((string text, string? secret) in all)
            {
                int y = 16 + row * lineHeight * 2; // blank line between entries so rows never merge
                row++;
                graphics.DrawString(text, font, brush, x, y);

                RectInt secretBox = RectInt.Empty;
                if (secret is not null)
                {
                    using var format = new StringFormat();
                    format.SetMeasurableCharacterRanges([new CharacterRange(text.IndexOf(secret, StringComparison.Ordinal), secret.Length)]);
                    var measured = graphics.MeasureCharacterRanges(text, font, new RectangleF(x, y, 4000, lineHeight * 2), format);
                    RectangleF bounds = measured[0].GetBounds(graphics);
                    foreach (var region in measured)
                    {
                        region.Dispose();
                    }
                    // The layout box of the characters; narrowed down to their actual ink below.
                    secretBox = new RectInt(
                        (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom))
                        .Intersect(new RectInt(0, 0, CanvasWidth, CanvasHeight));
                }

                drawn.Add(new DrawnLine(text, secret, side, new RectInt(0, y - 2, CanvasWidth, y + lineHeight), secretBox));
            }
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, CanvasWidth, CanvasHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[CanvasWidth * CanvasHeight * 4];
            for (int y = 0; y < CanvasHeight; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * CanvasWidth * 4, CanvasWidth * 4);
            }
            return (pixels, drawn.Select(line => line with { SecretBox = InkBounds(pixels, line.SecretBox, background) }).ToList());
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
