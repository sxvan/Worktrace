using Capture.Core.Geometry;
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
    public void Password_stated_in_prose_is_hidden_to_the_end_of_the_row()
    {
        Assert.Equal(["Tr0ub4dor&3", "for", "now"], Hidden("the wifi password is Tr0ub4dor&3 for now"));
        Assert.Equal(["correct", "horse", "battery"], Hidden("the wifi password is: correct horse battery"));
    }

    [Theory]
    [InlineData("whether the saved images show redaction and the password-field check works")]
    [InlineData("the password is stored as a salted hash")]
    [InlineData("secret santa list for the office party")]
    [InlineData("Change your password every 90 days.")]
    [InlineData("Forgot your password? Reset it here.")]
    [InlineData("the password manager opens in a separate window")]
    [InlineData("a secret Santa gift")]
    public void Prose_that_mentions_passwords_is_left_alone(string line)
    {
        Assert.Empty(Find(line));
    }

    [Theory]
    [InlineData("password hunter2", "hunter2")]            // separator lost, but the value has a digit
    [InlineData("password Som#mer", "Som#mer")]            // a symbol inside the word
    [InlineData("password hunterTwo", "hunterTwo")]        // inner capital
    public void Value_without_separator_is_hidden_only_if_it_looks_like_a_password(string line, string expectedHiddenWord)
    {
        Assert.Contains(expectedHiddenWord, Hidden(line));
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
}
