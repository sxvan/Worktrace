using Capture.Core.Capturing;
using Capture.Core.Geometry;
using Capture.Core.Pipeline;
using Capture.Core.Redaction;

namespace Capture.Core.Tests;

public class RedactionTests
{
    private const int CharWidth = 10;
    private const int LineHeight = 20;

    // Fake secrets are assembled at run time so no scanner mistakes this file for a leak.
    private static readonly string Body = string.Concat(Enumerable.Repeat("a1B2c3D4e5", 4));

    /// <summary>Lays text out like a monospace screen: each word gets a box at its character position.</summary>
    private static RecognizedLine Line(int row, string text)
    {
        var words = new List<RecognizedWord>();
        int column = 0;
        foreach (string word in text.Split(' '))
        {
            if (word.Length > 0)
            {
                words.Add(new RecognizedWord(word, new RectInt(
                    column * CharWidth, row * LineHeight, (column + word.Length) * CharWidth, (row + 1) * LineHeight)));
            }
            column += word.Length + 1;
        }
        return new RecognizedLine(words);
    }

    private static IReadOnlyList<RectInt> Find(params string[] lines) =>
        new HeuristicSecretDetector().FindSecrets(lines.Select((text, row) => Line(row, text)).ToList());

    /// <summary>The words whose boxes are covered by a redaction region.</summary>
    private static List<string> Hidden(params string[] lines)
    {
        var regions = Find(lines);
        return lines
            .SelectMany((text, row) => Line(row, text).Words)
            .Where(word => regions.Any(region => region.Contains(word.Bounds)))
            .Select(word => word.Text)
            .ToList();
    }

    // ---- Known token formats ----

    public static TheoryData<string> KnownTokens => new()
    {
        "sk-" + "ant-api03-" + Body,
        "gh" + "p_" + Body,
        "github" + "_pat_" + Body,
        "gl" + "pat-" + Body[..24],
        "xox" + "b-" + "123456789012-" + Body[..20],
        "AKIA" + "IOSFODNN7EXAMPLE",
        "AIza" + Body[..35],
        "hf" + "_" + Body[..34],
        "SG" + "." + Body[..22] + "." + Body,
        "dop" + "_v1_" + Body + Body,
        "pypi" + "-" + Body + Body,
        "GOC" + "SPX-" + Body[..28],
        "whs" + "ec_" + Body[..32],
        "sb" + "_secret_" + Body[..24],
        "123456789" + ":" + Body[..35],
        "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0" + "." + "abcDEF123456",
    };

    [Theory]
    [MemberData(nameof(KnownTokens))]
    public void Known_token_formats_are_hidden_wherever_they_appear(string token)
    {
        Assert.Equal([token], Hidden($"export const x = {token} ok"));
    }

    [Fact]
    public void Credentials_inside_a_url_are_hidden()
    {
        Assert.Equal(["postgres://admin:hunter2@db.example.com:5432/app"],
            Hidden("DATABASE_URL is postgres://admin:hunter2@db.example.com:5432/app today"));
    }

    [Fact]
    public void Authorization_header_value_is_hidden()
    {
        var hidden = Hidden("Authorization: Bearer abcdefghijklmnop1234567890 end");

        Assert.Contains("abcdefghijklmnop1234567890", hidden);
        Assert.DoesNotContain("end", hidden);
    }

    // ---- Assignments ----

    [Theory]
    [InlineData("password = hunter2", "hunter2")]
    [InlineData("password: correcthorse", "correcthorse")]
    [InlineData("DB_PASSWORD=sup3rs3cret", "DB_PASSWORD=sup3rs3cret")]
    [InlineData("\"client_secret\": \"abcd1234efgh\"", "\"abcd1234efgh\"")]
    [InlineData("api_key := Zx9Kq2Lm8Pw4", "Zx9Kq2Lm8Pw4")]
    [InlineData("$env:ACCESS_TOKEN = 'tok3nvalue99'", "'tok3nvalue99'")]
    public void Value_assigned_to_a_secret_name_is_hidden(string line, string expectedHiddenWord)
    {
        Assert.Contains(expectedHiddenWord, Hidden(line));
    }

    [Fact]
    public void Only_the_value_is_hidden_not_the_name()
    {
        Assert.Equal(["hunter2"], Hidden("the password = hunter2 was set"));
    }

    [Theory]
    [InlineData("maxTokens: number")]
    [InlineData("const token: string")]
    [InlineData("password = null")]
    [InlineData("usePassword: true")]
    [InlineData("tokenCount = 4096")]
    [InlineData("public string Password { get; set; }")]
    public void Ordinary_code_is_left_alone(string line)
    {
        Assert.Empty(Find(line));
    }

    // ---- Passwords given away by their context ----

    [Theory]
    [InlineData("export DB_PASS=Sommer2024!", "DB_PASS=Sommer2024!")]
    [InlineData("smtp-pass: Sommer2024!", "Sommer2024!")]
    [InlineData("pw = Sommer2024!", "Sommer2024!")]
    [InlineData("mysql -u root -pS3cretPass prod_db", "-pS3cretPass")]
    [InlineData("sshpass -p Sommer2024! ssh deploy@host", "Sommer2024!")]
    [InlineData("net use Z: \\\\fileserver\\share /user:corp\\svc_backup Winter!2026", "Winter!2026")]
    [InlineData("login: admin / Sommer2024!", "Sommer2024!")]
    [InlineData("mysql --password=Sommer2024! prod_db", "--password=Sommer2024!")]
    public void Password_is_found_from_its_context(string line, string expectedHiddenWord)
    {
        Assert.Contains(expectedHiddenWord, Hidden(line));
    }

    [Fact]
    public void Passphrase_in_prose_is_hidden_to_the_end_of_the_row()
    {
        Assert.Equal(["correct", "horse", "battery", "staple"], Hidden("the wifi password is correct horse battery staple"));
    }

    [Theory]
    [InlineData("all tests passed: 141 of 141")]
    [InlineData("bypass: enabled for admins")]
    [InlineData("mysql -u root -p prod_db")]                                   // asks for the password interactively
    [InlineData("pin: true")]
    [InlineData("password_hash = bcrypt.hashpw(pw, salt)")]                   // a function call, not a value
    [InlineData("docker run -e POSTGRES_PASSWORD_FILE=/run/secrets/db_password postgres:17")] // a file path
    [InlineData("user: alice / engineering team")]
    public void Lookalikes_of_password_contexts_are_left_alone(string line)
    {
        Assert.Empty(Find(line));
    }

    // ---- Text as recognition really returns it ----

    [Theory]
    [InlineData("password hunter2", "hunter2")]                                        // "=" lost
    [InlineData("\"client secret : \" \" Zx9Kq2Lm8Pw4Tr6Y\" ,", "Zx9Kq2Lm8Pw4Tr6Y\"")]   // "_" lost, quotes doubled
    [InlineData("\"client secret \"Zx9Kq2Lm8Pw4Tr6Y\",", "\"Zx9Kq2Lm8Pw4Tr6Y\",")]      // ":" lost
    [InlineData("= AKIAIOSFODNN7EXAPIPLE", "AKIAIOSFODNN7EXAPIPLE")]                   // name lost, letters misread
    [InlineData("aws access key id = Zx9Kq2Lm8Pw4", "Zx9Kq2Lm8Pw4")]                   // "_" read as spaces
    [InlineData("DATABASE_URL=postgres : / / admin : s3cretpw@db.internal : 5432/ app", "s3cretpw@db.internal")]
    [InlineData("DATABASE_URL=postgres : / / admin : s 3cretpQdb. internal : 5432/ app", "3cretpQdb.")]
    public void Secrets_are_still_found_in_damaged_text(string line, string expectedHiddenWord)
    {
        Assert.Contains(expectedHiddenWord, Hidden(line));
    }

    [Theory]
    [InlineData("open http://localhost:8080/health in the browser")]
    [InlineData("see https://example.com/docs: it explains everything")]
    [InlineData("server listening on http : / / localhost : 3000")]
    public void Urls_without_credentials_are_left_alone(string line)
    {
        Assert.Empty(Find(line));
    }

    // ---- Private keys ----

    [Fact]
    public void Every_line_of_a_private_key_block_is_hidden()
    {
        var regions = Find(
            "before",
            "-----BEGIN RSA PRIVATE KEY-----",
            "MIIEowIBAAKCAQEA",
            "short",
            "-----END RSA PRIVATE KEY-----",
            "after");

        Assert.Equal(4, regions.Count);
        Assert.DoesNotContain(regions, r => r.Top == 0);
        Assert.DoesNotContain(regions, r => r.Top == 5 * LineHeight);
    }

    // ---- Random-looking strings ----

    [Fact]
    public void Long_random_string_without_a_known_prefix_is_hidden()
    {
        string key = "Qw7Zr2Lp9Xk4Vb8Nm3Jh6Gf5Ds1Ay0TuE";

        Assert.Equal([key], Hidden($"key {key} here"));
    }

    [Theory]
    [InlineData("3f786850e387550fdab836ed7e6dc881de23001b")]                  // git commit hash
    [InlineData("SoftwareBitmapCreateCopyFromSurfaceAsyncOverload")]          // long identifier
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaAAAA1111")]              // long but repetitive
    public void Long_strings_that_are_not_random_are_left_alone(string text)
    {
        Assert.Empty(Find($"see {text} here"));
    }

    [Fact]
    public void Plain_text_is_left_alone()
    {
        Assert.Empty(Find("The quick brown fox jumps over the lazy dog.", "git commit -m \"fix: handle empty input\""));
    }

    // ---- Row merging ----

    private static RecognizedLine Fragment(int top, int left, string text) =>
        new([new RecognizedWord(text, new RectInt(left, top, left + text.Length * CharWidth, top + LineHeight))]);

    [Fact]
    public void Fragments_at_the_same_height_become_one_row_in_reading_order()
    {
        var rows = RowMerger.MergeRows([Fragment(100, 200, "hunter2"), Fragment(101, 0, "password"), Fragment(98, 100, "=")]);

        Assert.Equal(["password", "=", "hunter2"], Assert.Single(rows).Words.Select(w => w.Text));
    }

    [Fact]
    public void Fragments_at_different_heights_stay_separate_rows()
    {
        var rows = RowMerger.MergeRows([Fragment(0, 0, "first"), Fragment(LineHeight + 2, 0, "second"), Fragment(9, 300, "half")]);

        // "half" overlaps the first row by more than half its height, the second by less.
        Assert.Equal(2, rows.Count);
        Assert.Equal(["first", "half"], rows[0].Words.Select(w => w.Text));
        Assert.Equal(["second"], rows[1].Words.Select(w => w.Text));
    }

    [Fact]
    public async Task Secret_split_across_fragments_is_found_after_merging()
    {
        RecognizedLine[] fragments = [Fragment(40, 0, "password"), Fragment(40, 120, "hunter2")];
        var redactor = new TextRedactor(new FakeRecognizer(fragments), [new HeuristicSecretDetector()]);

        RedactionResult result = await redactor.RedactAsync(WhiteImage(300, 100), CancellationToken.None);

        using RedactedWindowImage image = Assert.IsType<RedactedWindowImage>(result.Image);
        Assert.Equal(1, image.RedactionCount);
    }

    // ---- TextRedactor ----

    private sealed class FakeRecognizer(IReadOnlyList<RecognizedLine>? lines) : ITextRecognizer
    {
        public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken) =>
            lines is null ? throw new InvalidOperationException("ocr unavailable") : Task.FromResult(lines);
    }

    private static RawWindowImage WhiteImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var bounds = new RectInt(0, 0, width, height);
        var window = new WindowInfo(1, bounds, bounds, 0, true, true, "Code.exe", "t");
        var monitor = new MonitorInfo(1, 1, "m", bounds, 1.0, true);
        return new RawWindowImage(DateTimeOffset.UnixEpoch, new WindowPlacement(window, [bounds], monitor), width, height, pixels);
    }

    [Fact]
    public async Task Detected_regions_are_blacked_out_and_the_rest_is_untouched()
    {
        var lines = new[] { Line(1, "password = hunter2") };
        var redactor = new TextRedactor(new FakeRecognizer(lines), [new HeuristicSecretDetector()]);

        RedactionResult result = await redactor.RedactAsync(WhiteImage(300, 100), CancellationToken.None);

        using RedactedWindowImage image = Assert.IsType<RedactedWindowImage>(result.Image);
        Assert.Equal(1, image.RedactionCount);
        RectInt secret = lines[0].Words[2].Bounds;
        byte At(int x, int y) => image.Pixels[(y * 300 + x) * 4];
        Assert.Equal(0, At(secret.Left, secret.Top));
        Assert.Equal(0, At(secret.Right - 1, secret.Bottom - 1));
        Assert.Equal(0, At(secret.Left - 2, secret.Top - 2));        // padding
        Assert.Equal(255, At(lines[0].Words[0].Bounds.Left, secret.Top)); // the word "password" stays
        Assert.Equal(255, At(5, 5));
    }

    [Fact]
    public async Task Region_at_the_image_edge_is_clipped()
    {
        var lines = new[] { Line(0, "password = hunter2") };
        var redactor = new TextRedactor(new FakeRecognizer(lines), [new HeuristicSecretDetector()]);

        // Image narrower than the text: the secret's box reaches past the right edge.
        RedactionResult result = await redactor.RedactAsync(WhiteImage(150, 20), CancellationToken.None);

        using RedactedWindowImage image = Assert.IsType<RedactedWindowImage>(result.Image);
        Assert.Equal(0, image.Pixels[(0 * 150 + 149) * 4]);
    }

    [Fact]
    public async Task Frame_is_dropped_when_the_text_cannot_be_read()
    {
        var redactor = new TextRedactor(new FakeRecognizer(null), [new HeuristicSecretDetector()]);
        using RawWindowImage raw = WhiteImage(10, 10);

        RedactionResult result = await redactor.RedactAsync(raw, CancellationToken.None);

        Assert.True(result.IsDropped);
        Assert.Contains("text recognition failed", result.DropReason);
    }

    [Fact]
    public async Task Image_without_secrets_passes_unchanged()
    {
        var redactor = new TextRedactor(new FakeRecognizer([Line(0, "hello world")]), [new HeuristicSecretDetector()]);

        RedactionResult result = await redactor.RedactAsync(WhiteImage(200, 40), CancellationToken.None);

        using RedactedWindowImage image = Assert.IsType<RedactedWindowImage>(result.Image);
        Assert.Equal(0, image.RedactionCount);
        Assert.All(image.Pixels, b => Assert.Equal(255, b));
    }
}
