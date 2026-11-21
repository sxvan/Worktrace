using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Pipeline;
using Capture.Core.Redaction;

namespace Capture.Core.Tests;

public class ScreenTextTests
{
    // Fake secrets are assembled at run time so no scanner mistakes this file for a leak.
    private static readonly string Token = "gh" + "p_" + string.Concat(Enumerable.Repeat("a1B2c3D4e5", 4));

    private static RecognizedWord Word(string text, int left, int top, int height = 20) =>
        new(text, new RectInt(left, top, left + text.Length * 10, top + height));

    // ---- Titles (layer 1) ----

    [Fact]
    public void Title_without_secrets_is_unchanged()
    {
        Assert.Equal("Redaction.cs - open-timesheet - Visual Studio Code",
            TextScrubber.Default.Scrub("Redaction.cs - open-timesheet - Visual Studio Code"));
    }

    [Fact]
    public void Token_in_a_title_is_replaced()
    {
        Assert.Equal("export GH_TOKEN= [redacted] - Terminal",
            TextScrubber.Default.Scrub($"export GH_TOKEN= {Token} - Terminal"));
    }

    [Fact]
    public void Password_in_a_title_is_replaced()
    {
        string? scrubbed = TextScrubber.Default.Scrub("notes.txt: password = hunter2 - Notepad");
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.Contains(TextScrubber.RedactedMarker, scrubbed);
    }

    [Fact]
    public void Null_and_empty_titles_stay_as_they_are()
    {
        Assert.Null(TextScrubber.Default.Scrub(null));
        Assert.Equal("", TextScrubber.Default.Scrub(""));
    }

    [Fact]
    public void Url_queries_are_kept_by_default()
    {
        Assert.Equal("https://example.com/search?q=report - Edge", TextScrubber.Default.Scrub("https://example.com/search?q=report - Edge"));
    }

    [Fact]
    public void Url_queries_are_cut_when_asked()
    {
        var scrubber = new TextScrubber([new HeuristicSecretDetector()], stripUrlQueries: true);
        Assert.Equal("https://example.com/search - Edge", scrubber.Scrub("https://example.com/search?q=report - Edge"));
    }

    [Theory]
    [InlineData("https://example.com/callback?code=abc123&state=x", "https://example.com/callback")]
    [InlineData("see https://example.com/a#access_token=xyz now", "see https://example.com/a now")]
    [InlineData("https://github.com/org/repo/pull/12", "https://github.com/org/repo/pull/12")]
    [InlineData("PR #3 - github.com/org/repo?token=abc - Edge", "PR #3 - github.com/org/repo - Edge")]
    [InlineData("no url? here", "no url? here")]
    [InlineData("Is it config.json? yes", "Is it config.json? yes")]
    public void Url_queries_and_fragments_are_cut(string input, string expected)
    {
        Assert.Equal(expected, TextScrubber.StripUrlQueries(input));
    }

    // ---- Screen text (layer 2) ----

    [Fact]
    public void Row_is_split_at_wide_gaps()
    {
        // A sidebar entry at the left, the editor line far to the right on the same row.
        RecognizedLine row = new([Word("EXPLORER", 0, 0), Word("public", 400, 0), Word("interface", 470, 0)]);

        IReadOnlyList<ScreenTextLine> text = ScreenText.Build([row], [], 1000, 500, TextScrubber.Default);

        Assert.Equal(["EXPLORER", "public interface"], text.Select(t => t.Text));
    }

    [Fact]
    public void Boxes_are_relative_to_the_image()
    {
        RecognizedLine row = new([Word("hello", 100, 50)]);

        ScreenTextLine line = Assert.Single(ScreenText.Build([row], [], 1000, 500, TextScrubber.Default));

        Assert.Equal(0.1, line.Left);
        Assert.Equal(0.1, line.Top);
        Assert.Equal(0.15, line.Right);
        Assert.Equal(0.14, line.Bottom);
    }

    [Fact]
    public void Words_inside_secret_regions_become_one_marker()
    {
        RecognizedLine row = new([Word("key", 0, 0), Word("abc", 40, 0), Word("def", 80, 0), Word("end", 120, 0)]);
        RectInt secret = new(40, 0, 110, 20);

        ScreenTextLine line = Assert.Single(ScreenText.Build([row], [secret], 1000, 500, TextScrubber.Default));

        Assert.Equal("key [redacted] end", line.Text);
    }

    private sealed class FakeRecognizer(IReadOnlyList<RecognizedLine> lines) : ITextRecognizer
    {
        public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken) =>
            Task.FromResult(lines);
    }

    [Fact]
    public async Task Redactor_hands_over_the_scrubbed_text()
    {
        RecognizedLine row = new([Word("password", 0, 0), Word("=", 90, 0), Word("hunter2", 110, 0)]);
        var redactor = new TextRedactor(new FakeRecognizer([row]), [new HeuristicSecretDetector()]);

        var bounds = new RectInt(0, 0, 300, 40);
        var window = new WindowInfo(1, bounds, bounds, 0, true, true, "Code.exe", "t");
        var monitor = new MonitorInfo(1, 1, "m", bounds, 1.0, true);
        using var raw = new RawWindowImage(
            DateTimeOffset.UnixEpoch, new WindowPlacement(window, [bounds], monitor), 300, 40, new byte[300 * 40 * 4]);

        RedactionResult result = await redactor.RedactAsync(raw, CancellationToken.None);

        using RedactedWindowImage image = Assert.IsType<RedactedWindowImage>(result.Image);
        ScreenTextLine line = Assert.Single(image.Text!);
        Assert.Equal("password = [redacted]", line.Text);
    }
}
