using System.Text.RegularExpressions;
using Capture.Core.Config;

namespace Capture.Core.Allowlist;

/// <summary>Decides whether a window may be recorded. Anything without a matching rule is not allowed.</summary>
public sealed class AllowlistMatcher
{
    private static readonly string[] PrivateWindowMarkers =
    [
        "InPrivate", "Incognito", "Inkognito", "Private Browsing", "Private Window", "Privater Modus", "Privates Fenster",
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Process name (without .exe) -> one entry per rule; a null entry is a rule without title patterns.
    private readonly Dictionary<string, List<Regex[]?>> _rules = new(StringComparer.OrdinalIgnoreCase);

    public AllowlistMatcher(IEnumerable<AllowlistRule> rules)
    {
        foreach (AllowlistRule rule in rules)
        {
            string process = NormalizeProcess(rule.Process);
            if (process.Length == 0)
            {
                continue;
            }

            Regex[]? patterns = rule.TitlePatterns is { Count: > 0 }
                ? rule.TitlePatterns.Select(ToRegex).ToArray()
                : null;

            if (!_rules.TryGetValue(process, out List<Regex[]?>? list))
            {
                _rules[process] = list = [];
            }
            list.Add(patterns);
        }
    }

    /// <summary>
    /// True if any rule names this process. Callers use this to avoid even reading the title of
    /// windows that can never be allowed.
    /// </summary>
    public bool HasRuleFor(string processName) => _rules.ContainsKey(NormalizeProcess(processName));

    public bool IsAllowed(string processName, string? title)
    {
        if (!_rules.TryGetValue(NormalizeProcess(processName), out List<Regex[]?>? rules))
        {
            return false;
        }

        title ??= "";
        if (IsPrivateWindowTitle(title))
        {
            return false;
        }

        foreach (Regex[]? patterns in rules)
        {
            if (patterns is null)
            {
                return true;
            }

            foreach (Regex pattern in patterns)
            {
                try
                {
                    if (pattern.IsMatch(title))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Fail closed: an undecidable match is not a match.
                }
            }
        }

        return false;
    }

    /// <summary>Private browser windows are never allowed, regardless of rules.</summary>
    public static bool IsPrivateWindowTitle(string title)
    {
        foreach (string marker in PrivateWindowMarkers)
        {
            if (title.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeProcess(string? process)
    {
        process = process?.Trim() ?? "";
        return process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
    }

    private static Regex ToRegex(string wildcard)
    {
        string pattern = "^" + Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout);
    }
}
