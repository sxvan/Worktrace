using System.Text.RegularExpressions;
using Capture.Core.Config;

namespace Capture.Core.Rules;

/// <summary>What may be recorded about a window.</summary>
public enum WindowMode
{
    /// <summary>No include rule matches, or an exclude takes everything away: nothing is recorded, not even the process name.</summary>
    Other,

    /// <summary>The process name (and the title, if allowed) is logged. Never captured.</summary>
    Log,

    /// <summary>Process name and title are logged and the window is captured.</summary>
    Capture,
}

/// <param name="LogTitle">Whether the window title may be stored. Always true for <see cref="WindowMode.Capture"/>.</param>
public readonly record struct WindowClassification(WindowMode Mode, bool LogTitle)
{
    public static WindowClassification Other => new(WindowMode.Other, false);
}

/// <summary>
/// Decides what may be recorded about a window. The strongest matching include rule grants a level;
/// every matching exclude rule takes its level and everything above it away. Excludes always win, and
/// a window without a matching include rule is <see cref="WindowMode.Other"/>.
/// </summary>
public sealed class WindowRuleMatcher
{
    private static readonly string[] PrivateWindowMarkers =
    [
        "InPrivate", "Incognito", "Inkognito", "Private Browsing", "Private Window", "Privater Modus", "Privates Fenster",
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The levels a rule can name, weakest first.</summary>
    private enum Level
    {
        Other,
        Log,
        LogWithTitles,
        Capture,
    }

    private sealed record Rule(string Process, Level Level, Regex[]? TitlePatterns);

    /// <summary>A rule with this process name applies to every application.</summary>
    public const string AnyProcess = "*";

    private readonly List<Rule> _include;
    private readonly List<Rule> _exclude;

    public WindowRuleMatcher(IEnumerable<WindowRule> include, IEnumerable<WindowRule>? exclude = null)
    {
        _include = Parse(include);
        _exclude = Parse(exclude ?? []);
    }

    public WindowRuleMatcher(CaptureConfig config) : this(config.Include, config.Exclude)
    {
    }

    /// <summary>
    /// True if a window of this process can be recorded at all. Callers use this to avoid even reading
    /// the title of windows that never can.
    /// </summary>
    public bool HasRuleFor(string processName)
    {
        string process = NormalizeProcess(processName);
        if (!_include.Any(rule => AppliesTo(rule, process)))
        {
            return false;
        }
        // An exclude that takes everything away from every window of the process.
        return !_exclude.Any(rule => rule.Level == Level.Log && rule.TitlePatterns is null && AppliesTo(rule, process));
    }

    /// <summary>True if the window may be captured.</summary>
    public bool ShouldCapture(string processName, string? title) => Classify(processName, title).Mode == WindowMode.Capture;

    public WindowClassification Classify(string processName, string? title)
    {
        title ??= "";
        if (IsPrivateWindowTitle(title))
        {
            return WindowClassification.Other;
        }

        string process = NormalizeProcess(processName);
        Level granted = Level.Other;
        foreach (Rule rule in _include)
        {
            // The catch-all skips untitled windows: those are helpers and overlays.
            bool skip = rule.Process == AnyProcess && title.Length == 0;
            if (rule.Level > granted && !skip && AppliesTo(rule, process) && Matches(rule, title, isExclude: false))
            {
                granted = rule.Level;
            }
        }

        foreach (Rule rule in _exclude)
        {
            if (granted == Level.Other)
            {
                break;
            }
            if (rule.Level <= granted && AppliesTo(rule, process) && Matches(rule, title, isExclude: true))
            {
                granted = rule.Level - 1;
            }
        }

        return granted switch
        {
            Level.Capture => new WindowClassification(WindowMode.Capture, true),
            Level.LogWithTitles => new WindowClassification(WindowMode.Log, true),
            Level.Log => new WindowClassification(WindowMode.Log, false),
            _ => WindowClassification.Other,
        };
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

    private static List<Rule> Parse(IEnumerable<WindowRule> rules)
    {
        var parsed = new List<Rule>();
        foreach (WindowRule rule in rules)
        {
            string process = NormalizeProcess(rule.Process);
            Level? level = rule.Mode?.Trim().ToLowerInvariant() switch
            {
                null or "" or "capture" => Level.Capture,
                "logwithtitles" => Level.LogWithTitles,
                "log" => Level.Log,
                _ => null, // unknown mode: ignore the rule instead of guessing
            };
            if (process.Length == 0 || level is null)
            {
                continue;
            }

            Regex[]? patterns = rule.TitlePatterns is { Count: > 0 }
                ? rule.TitlePatterns.Select(ToRegex).ToArray()
                : null;
            parsed.Add(new Rule(process, level.Value, patterns));
        }
        return parsed;
    }

    private static bool AppliesTo(Rule rule, string process) =>
        rule.Process == AnyProcess || string.Equals(rule.Process, process, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rule covers the title: a title pattern matches (or it has none). A pattern that times out
    /// counts as a match for an exclude rule and as no match for an include rule: both fail closed.
    /// </summary>
    private static bool Matches(Rule rule, string title, bool isExclude)
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
                if (isExclude)
                {
                    return true;
                }
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
