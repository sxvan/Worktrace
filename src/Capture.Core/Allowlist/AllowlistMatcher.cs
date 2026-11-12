using System.Text.RegularExpressions;
using Capture.Core.Config;

namespace Capture.Core.Allowlist;

/// <summary>What may be recorded about a window.</summary>
public enum WindowMode
{
    /// <summary>No rule matches: nothing is recorded, not even the process name.</summary>
    Other,

    /// <summary>The process name (and the title, if the rule says so) is logged. Never captured.</summary>
    Log,

    /// <summary>Process name and title are logged and the window is captured.</summary>
    Capture,
}

/// <param name="LogTitle">Whether the window title may be stored. Always true for <see cref="WindowMode.Capture"/>.</param>
public readonly record struct WindowClassification(WindowMode Mode, bool LogTitle)
{
    public static WindowClassification Other => new(WindowMode.Other, false);
}

/// <summary>Decides what may be recorded about a window. Anything without a matching rule is <see cref="WindowMode.Other"/>.</summary>
public sealed class AllowlistMatcher
{
    private static readonly string[] PrivateWindowMarkers =
    [
        "InPrivate", "Incognito", "Inkognito", "Private Browsing", "Private Window", "Privater Modus", "Privates Fenster",
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private sealed record Rule(WindowMode Mode, bool LogTitle, Regex[]? TitlePatterns);

    // Keyed by process name without ".exe".
    private readonly Dictionary<string, List<Rule>> _rules = new(StringComparer.OrdinalIgnoreCase);

    public AllowlistMatcher(IEnumerable<WindowRule> rules)
    {
        foreach (WindowRule rule in rules)
        {
            string process = NormalizeProcess(rule.Process);
            WindowMode? mode = rule.Mode?.Trim().ToLowerInvariant() switch
            {
                null or "" or "capture" => WindowMode.Capture,
                "log" => WindowMode.Log,
                _ => null, // unknown mode: ignore the rule instead of guessing
            };
            if (process.Length == 0 || mode is null)
            {
                continue;
            }

            Regex[]? patterns = rule.TitlePatterns is { Count: > 0 }
                ? rule.TitlePatterns.Select(ToRegex).ToArray()
                : null;

            if (!_rules.TryGetValue(process, out List<Rule>? list))
            {
                _rules[process] = list = [];
            }
            list.Add(new Rule(mode.Value, mode == WindowMode.Capture || rule.LogTitles, patterns));
        }
    }

    /// <summary>
    /// True if any rule names this process. Callers use this to avoid even reading the title of
    /// windows that can never be recorded.
    /// </summary>
    public bool HasRuleFor(string processName) => _rules.ContainsKey(NormalizeProcess(processName));

    /// <summary>True if the window may be captured.</summary>
    public bool IsAllowed(string processName, string? title) => Classify(processName, title).Mode == WindowMode.Capture;

    /// <summary>The strongest matching rule wins: capture over log, and a rule that logs titles over one that does not.</summary>
    public WindowClassification Classify(string processName, string? title)
    {
        if (!_rules.TryGetValue(NormalizeProcess(processName), out List<Rule>? rules))
        {
            return WindowClassification.Other;
        }

        title ??= "";
        if (IsPrivateWindowTitle(title))
        {
            return WindowClassification.Other;
        }

        WindowClassification best = WindowClassification.Other;
        foreach (Rule rule in rules)
        {
            if (!Matches(rule, title))
            {
                continue;
            }
            if (rule.Mode > best.Mode || (rule.Mode == best.Mode && rule.LogTitle && !best.LogTitle))
            {
                best = new WindowClassification(rule.Mode, rule.LogTitle);
            }
        }
        return best;
    }

    /// <summary>Private browser windows are never recorded, regardless of rules.</summary>
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

    private static bool Matches(Rule rule, string title)
    {
        if (rule.TitlePatterns is null)
        {
            return true;
        }

        foreach (Regex pattern in rule.TitlePatterns)
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
