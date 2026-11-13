using System.Text;
using System.Text.RegularExpressions;
using Capture.Core.Geometry;

namespace Capture.Core.Redaction;

/// <summary>
/// Pattern-based secret detection on recognized text. Errs on the side of blacking out too much:
/// a hidden word costs nothing, a saved secret does.
///
/// Finds: well-known token formats, values assigned to names like "password" or "api_key",
/// Authorization headers, credentials inside URLs, private key blocks, and long random-looking strings.
/// </summary>
public sealed class HeuristicSecretDetector : ISecretDetector
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private const int MaxPrivateKeyLines = 80;

    /// <summary>Formats that are secrets wherever they appear. The whole match is hidden.</summary>
    private static readonly Regex[] KnownTokens =
    [
        new(@"\bsk-[A-Za-z0-9_\-]{16,}", Options, Timeout),                               // OpenAI, Anthropic
        new(@"\b(sk|pk|rk)_(live|test)_[A-Za-z0-9]{16,}", Options, Timeout),              // Stripe
        new(@"\bwhsec_[A-Za-z0-9]{16,}", Options, Timeout),                               // Stripe webhook secret
        new(@"\bhf_[A-Za-z0-9]{30,}", Options, Timeout),                                  // Hugging Face
        new(@"\bsb_(publishable|secret)_[A-Za-z0-9_\-]{5,}", Options, Timeout),           // Supabase
        new(@"\bSG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,}", Options, Timeout),         // SendGrid
        new(@"\bSK[0-9a-fA-F]{32}", Options, Timeout),                                    // Twilio
        new(@"\bdop_v1_[A-Za-z0-9]{40,}", Options, Timeout),                              // DigitalOcean
        new(@"\bpypi-[A-Za-z0-9_\-]{40,}", Options, Timeout),                             // PyPI
        new(@"\bGOCSPX-[A-Za-z0-9_\-]{20,}", Options, Timeout),                           // Google OAuth client secret
        new(@"\bvcp_[A-Za-z0-9_\-]{20,}", Options, Timeout),                              // Vercel
        new(@"\bph[xc]_[A-Za-z0-9_]{40,}", Options, Timeout),                             // PostHog
        new(@"\b\d{8,10}:[A-Za-z0-9_\-]{30,}", Options, Timeout),                         // Telegram bot token
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Options, Timeout),                           // GitHub
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}", Options, Timeout),
        new(@"\bgl(pat|cbt|oas|soat)-[A-Za-z0-9_\-]{16,}", Options, Timeout),             // GitLab
        new(@"\bnpm_[A-Za-z0-9]{30,}", Options, Timeout),
        new(@"\b(xox[abeprs]|xapp)-[A-Za-z0-9\-]{10,}", Options, Timeout),                // Slack
        new(@"\b(AKIA|ASIA)[0-9A-Za-z]{12,}", Options, Timeout),                          // AWS access key id
        new(@"\b(?=[A-Z0-9]*\d)[A-Z0-9]{16,}", Options, Timeout),                         // long run of capitals and digits
        new(@"\bAIza[0-9A-Za-z_\-]{30,}", Options, Timeout),                              // Google API key
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}(\.[A-Za-z0-9_\-]*)?", Options, Timeout), // JWT
        new(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-]{16,}=*", Options | RegexOptions.IgnoreCase, Timeout),
    ];

    // Text recognition regularly drops or changes punctuation: "_" becomes a space, "=" and ":"
    // vanish, quotes double up. The patterns below are written to survive that.

    /// <summary>
    /// A name that says "password" or "secret", then the value. The separator may be missing
    /// altogether, so plain prose ("password reset") also loses a word now and then.
    /// </summary>
    private static readonly Regex PasswordAssignment = new(
        @"(?<kind>password|passwd|pwd|passphrase|secret)[\w.\-]*(?<sep>[\s""'`:=.>\-]{1,8})(?<value>[^\s""',;]{4,})",
        Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>name = value for names like token or api_key. Needs a separator, and the value is checked further.</summary>
    private static readonly Regex KeyAssignment = new(
        @"(?<kind>token|api[_\-\s]?key|access[_\-\s]?key|private[_\-\s]?key|credential)(?:[_\-\s]?[A-Za-z0-9]+){0,2}" +
        @"[""'\s]*(=>|:=|=|:)[""'\s]*(?<value>[^\s""',;]{4,})",
        Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>Other places where the context says "the next thing is a password". Only the value is hidden.</summary>
    private static readonly Regex[] PasswordContexts =
    [
        // DB_PASS=..., smtp-pass: ..., pw = ..., PIN: ...  (the short name must stand alone, so "bypass" or "passed" do not count)
        new(@"(?<![A-Za-z])(?:pass|pw|pin)(?![A-Za-z])[\w.\-]*[""'\s]*(?:=|:)[""'\s]*(?<value>[^\s""',;]{4,})",
            Options | RegexOptions.IgnoreCase, Timeout),
        // mysql -u root -pS3cret
        new(@"\b(?:mysql|mysqldump|mysqladmin|mariadb)\b.*?\s-p(?<value>[^\s]{3,})", Options | RegexOptions.IgnoreCase, Timeout),
        // sshpass -p 'S3cret' ssh ...
        new(@"\bsshpass\s+-p\s*[""']?(?<value>[^\s""']{3,})", Options | RegexOptions.IgnoreCase, Timeout),
        // net use ... /user:corp\name S3cret
        new(@"/user:\S+\s+(?<value>[^\s/]{4,})", Options | RegexOptions.IgnoreCase, Timeout),
        // login: admin / S3cret  (the value needs a digit or symbol, so "user: alice / engineering" stays)
        new(@"\b(?:login|user(?:name)?|credentials?)\s*[:=]\s*\S+\s*/\s*(?<value>(?=\S*[^\sA-Za-z])\S{4,})", Options | RegexOptions.IgnoreCase, Timeout),
    ];

    /// <summary>"the password is ..." in prose: everything after it on the row is hidden, since a passphrase can be several words.</summary>
    private static readonly Regex PasswordInProse = new(
        @"\b(?:password|passwort|passphrase|passcode)\s+(?:is|was|ist|lautet)\b\s*:?\s*(?<value>\S.*)$",
        Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>A function call or a file path where a value is expected: code, not a secret.</summary>
    private static readonly Regex CodeNotSecret = new(
        @"^(?:[A-Za-z_][\w.]*\(|/|\./|~/|[A-Za-z]:\\)", Options, Timeout);

    /// <summary>user:password@host inside a URL. The whole URL word is hidden.</summary>
    private static readonly Regex UrlCredentials = new(
        @"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s:/@]+:[^\s@/]+@\S*", Options, Timeout);

    /// <summary>
    /// The same, as text recognition tends to return it: spaces around ":" and "/", "@" misread.
    /// Anything after "//name:" that is not a port number counts; the rest of the line is hidden.
    /// </summary>
    private static readonly Regex DamagedUrlCredentials = new(
        @":\s?/\s?/\s?[^\s:/@]+\s?:\s?(?!\d{1,5}(?:\s|/|$))\S", Options, Timeout);

    private static readonly Regex RandomLooking = new(@"[A-Za-z0-9+/=_\-]{32,}", Options, Timeout);

    private static readonly HashSet<string> HarmlessValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "none", "nil", "undefined", "string", "str", "int", "bool", "boolean",
        "number", "object", "char", "byte", "bytes", "required", "optional", "void", "self", "this",
    };

    public IReadOnlyList<RectInt> FindSecrets(IReadOnlyList<RecognizedLine> lines)
    {
        var regions = new List<RectInt>();
        int privateKeyLinesLeft = 0;

        foreach (RecognizedLine line in lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            (string text, int[] wordStarts) = Join(line);

            // Private key blocks: hide every line from BEGIN to END.
            bool begins = text.Contains("BEGIN", StringComparison.OrdinalIgnoreCase)
                          && text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
            if (begins)
            {
                privateKeyLinesLeft = MaxPrivateKeyLines;
            }
            if (privateKeyLinesLeft > 0)
            {
                regions.Add(BoundsOf(line.Words));
                privateKeyLinesLeft--;
                if (!begins && text.Contains("END", StringComparison.OrdinalIgnoreCase)
                            && text.Contains("KEY", StringComparison.OrdinalIgnoreCase))
                {
                    privateKeyLinesLeft = 0;
                }
                continue;
            }

            try
            {
                foreach (Regex pattern in KnownTokens)
                {
                    foreach (Match match in pattern.Matches(text))
                    {
                        Add(regions, line, wordStarts, match.Index, match.Length);
                    }
                }

                foreach (Regex assignment in (Regex[])[PasswordAssignment, KeyAssignment])
                {
                    foreach (Match match in assignment.Matches(text))
                    {
                        Group value = match.Groups["value"];
                        if (!IsSecretValue(match.Groups["kind"].Value, value.Value))
                        {
                            continue;
                        }

                        // Without "=" or ":" this may just be prose ("the password-field check",
                        // "secret santa"). Then only a value that looks like a password counts.
                        Group separator = match.Groups["sep"];
                        bool explicitSeparator = !separator.Success || separator.Value.Contains('=') || separator.Value.Contains(':');
                        if (explicitSeparator || LooksLikePassword(value.Value))
                        {
                            Add(regions, line, wordStarts, value.Index, value.Length);
                        }
                    }
                }

                foreach (Regex context in PasswordContexts)
                {
                    foreach (Match match in context.Matches(text))
                    {
                        Group value = match.Groups["value"];
                        if (IsSecretValue("password", value.Value))
                        {
                            Add(regions, line, wordStarts, value.Index, value.Length);
                        }
                    }
                }

                if (PasswordInProse.Match(text) is { Success: true } prose)
                {
                    // "the password is stored as a hash" is ordinary prose; "the password is Tr0ub4dor&3" is not.
                    Group value = prose.Groups["value"];
                    string firstWord = value.Value.Split(' ', 2)[0];
                    if (prose.Value.Contains(':') || LooksLikePassword(firstWord))
                    {
                        Add(regions, line, wordStarts, value.Index, value.Length);
                    }
                }

                MatchCollection urls = UrlCredentials.Matches(text);
                foreach (Match match in urls)
                {
                    Add(regions, line, wordStarts, match.Index, match.Length);
                }
                if (urls.Count == 0 && DamagedUrlCredentials.Match(text) is { Success: true } damaged)
                {
                    Add(regions, line, wordStarts, damaged.Index, text.Length - damaged.Index);
                }

                foreach (Match match in RandomLooking.Matches(text))
                {
                    if (LooksRandom(match.Value))
                    {
                        Add(regions, line, wordStarts, match.Index, match.Length);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A line that cannot be judged is hidden completely.
                regions.Add(BoundsOf(line.Words));
            }
        }

        return regions;
    }

    private static bool IsSecretValue(string kind, string value)
    {
        value = value.Trim('"', '\'', '`', '(', ')', '{', '}', '[', ']', '<', '>');
        if (value.Length < 3 || HarmlessValues.Contains(value) || CodeNotSecret.IsMatch(value))
        {
            return false;
        }

        // Passwords can be anything. For "token", "key" and the like, plain words are far more
        // often code than secrets (maxTokens: number), so those need a digit or mixed case.
        bool passwordLike = kind.StartsWith("pass", StringComparison.OrdinalIgnoreCase)
                            || kind.Equals("pwd", StringComparison.OrdinalIgnoreCase)
                            || kind.Equals("secret", StringComparison.OrdinalIgnoreCase);
        if (passwordLike)
        {
            return true;
        }

        bool hasDigit = value.Any(char.IsAsciiDigit);
        bool mixedCase = value.Any(char.IsAsciiLetterLower) && value.Any(char.IsAsciiLetterUpper);
        return value.Length >= 8 && (hasDigit || mixedCase) && !value.All(char.IsAsciiDigit);
    }

    /// <summary>A digit, a symbol, or mixed case inside the word: more likely a password than an English word.</summary>
    private static bool LooksLikePassword(string value)
    {
        value = value.Trim('"', '\'', '`', '.', ',', ';', ':', '!', '?', '(', ')');
        if (value.Length < 4)
        {
            return false;
        }
        bool hasDigit = value.Any(char.IsAsciiDigit);
        bool hasSymbol = value.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '\'');
        // Upper case after the first letter ("hunterTwo"), not a capitalised word ("Reset").
        bool innerUpper = value.Skip(1).Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiLetterLower);
        return hasDigit || hasSymbol || innerUpper;
    }

    /// <summary>Long strings mixing upper case, lower case and digits with high entropy: keys without a known prefix.</summary>
    private static bool LooksRandom(string value)
    {
        int digits = value.Count(char.IsAsciiDigit);
        if (digits < 3 || !value.Any(char.IsAsciiLetterLower) || !value.Any(char.IsAsciiLetterUpper))
        {
            return false;
        }

        Span<int> counts = stackalloc int[128];
        foreach (char c in value)
        {
            counts[c & 127]++;
        }
        double entropy = 0;
        foreach (int count in counts)
        {
            if (count > 0)
            {
                double p = (double)count / value.Length;
                entropy -= p * Math.Log2(p);
            }
        }
        return entropy >= 4.0;
    }

    /// <summary>Joins the words with single spaces and remembers where each word starts.</summary>
    private static (string Text, int[] WordStarts) Join(RecognizedLine line)
    {
        var builder = new StringBuilder();
        int[] starts = new int[line.Words.Count];
        for (int i = 0; i < line.Words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }
            starts[i] = builder.Length;
            builder.Append(line.Words[i].Text);
        }
        return (builder.ToString(), starts);
    }

    /// <summary>Hides every word that overlaps the character range. Words cannot be split: positions inside a word are unknown.</summary>
    private static void Add(List<RectInt> regions, RecognizedLine line, int[] wordStarts, int index, int length)
    {
        var hit = new List<RecognizedWord>();
        for (int i = 0; i < line.Words.Count; i++)
        {
            int start = wordStarts[i], end = start + line.Words[i].Text.Length;
            if (start < index + length && end > index)
            {
                hit.Add(line.Words[i]);
            }
        }
        if (hit.Count > 0)
        {
            regions.Add(BoundsOf(hit));
        }
    }

    private static RectInt BoundsOf(IReadOnlyList<RecognizedWord> words) => new(
        words.Min(w => w.Bounds.Left), words.Min(w => w.Bounds.Top),
        words.Max(w => w.Bounds.Right), words.Max(w => w.Bounds.Bottom));
}
