using Capture.Core.Geometry;
using Capture.Core.Redaction;

namespace Capture.Core.Tests;

public class ScreenTextRedactorTests
{
    private static readonly ScreenTextRedactor Redactor = new(TextScrubber.Default);

    [Fact]
    public void Words_are_spread_over_the_line_by_characters()
    {
        var words = ScreenTextRedactor.Words(new OcrLine("ab  cdef", new RectInt(100, 10, 180, 30), 1f));

        Assert.Equal(["ab", "cdef"], words.Select(w => w.Text));
        Assert.Equal(new RectInt(100, 10, 120, 30), words[0].Bounds);
        Assert.Equal(new RectInt(140, 10, 180, 30), words[1].Bounds);
    }

    [Fact]
    public void Plain_text_is_kept_and_nothing_is_hidden()
    {
        var result = Redactor.Redact([new OcrLine("git commit -m \"fix: parse dates\"", new RectInt(0, 0, 320, 20), 0.98f)]);

        Assert.Equal("git commit -m \"fix: parse dates\"", Assert.Single(result.Lines).Text);
        Assert.Empty(result.HiddenRegions);
    }

    [Fact]
    public void A_token_is_replaced_in_the_text_and_blacked_out_in_the_image()
    {
        // 40 characters over 400 pixels: 10 pixels per character. The token starts at character 10.
        string line = "API_KEY = ghp_abcdefghijklmnopqrstuvwxyz0123";
        var bounds = new RectInt(0, 100, line.Length * 10, 120);

        var result = Redactor.Redact([new OcrLine(line, bounds, 0.97f)]);

        Assert.Equal("API_KEY = [redacted]", Assert.Single(result.Lines).Text);
        RectInt hidden = Assert.Single(result.HiddenRegions);
        // The token is covered, with some margin; the name before it is not.
        Assert.True(hidden.Left <= 100 && hidden.Right >= bounds.Right, hidden.ToString());
        Assert.True(hidden.Left > 50, hidden.ToString());
        Assert.True(hidden.Top <= 100 && hidden.Bottom >= 120, hidden.ToString());
    }

    [Fact]
    public void Lines_keep_their_boxes()
    {
        var a = new RectInt(0, 0, 50, 10);
        var b = new RectInt(0, 20, 50, 30);

        var result = Redactor.Redact([new OcrLine("one", a, 1f), new OcrLine("two", b, 1f)]);

        Assert.Equal([a, b], result.Lines.Select(l => l.Bounds));
    }

    [Fact]
    public void Pieces_on_one_row_are_joined_left_to_right_and_rows_go_top_to_bottom()
    {
        OcrLine[] pieces =
        [
            new("second", new RectInt(0, 40, 60, 60), 1f),
            new("world", new RectInt(80, 2, 130, 20), 1f),
            new("hello", new RectInt(0, 0, 50, 20), 1f),
        ];

        var result = Redactor.Redact(pieces);

        Assert.Equal(["hello world", "second"], result.Lines.Select(l => l.Text));
        Assert.Equal(new RectInt(0, 0, 130, 20), result.Lines[0].Bounds);
    }

    [Fact]
    public void Columns_on_the_same_row_become_separate_lines()
    {
        // A sidebar entry at the left, the main text far to the right, both 20 pixels high.
        OcrLine[] pieces =
        [
            new("main text", new RectInt(1000, 0, 1200, 20), 1f),
            new("sidebar", new RectInt(10, 2, 150, 22), 1f),
            new("continues", new RectInt(1230, 0, 1400, 20), 1f),
        ];

        var result = Redactor.Redact(pieces);

        Assert.Equal(["sidebar", "main text continues"], result.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_password_split_from_its_name_is_still_hidden()
    {
        // The recognizer returned "password:" and the value as two pieces.
        OcrLine[] pieces =
        [
            new("password:", new RectInt(0, 0, 90, 20), 1f),
            new("Hunter2Secret!", new RectInt(120, 0, 260, 20), 1f),
        ];

        var result = Redactor.Redact(pieces);

        Assert.Equal("password: [redacted]", Assert.Single(result.Lines).Text);
        RectInt hidden = Assert.Single(result.HiddenRegions);
        Assert.True(hidden.Left <= 120 && hidden.Right >= 260 && hidden.Left > 90, hidden.ToString());
    }
}
