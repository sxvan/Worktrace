using Capture.Core.Allowlist;
using Capture.Core.Config;

namespace Capture.Core.Tests;

public class AllowlistMatcherTests
{
    private static AllowlistMatcher Matcher(params AllowlistRule[] rules) => new(rules);

    [Theory]
    [InlineData("Code.exe")]
    [InlineData("code.EXE")]
    [InlineData("Code")]
    public void Process_match_ignores_case_and_exe_suffix(string process)
    {
        var matcher = Matcher(new AllowlistRule { Process = "Code.exe" });

        Assert.True(matcher.HasRuleFor(process));
        Assert.True(matcher.IsAllowed(process, "anything"));
    }

    [Fact]
    public void Unknown_process_is_not_allowed()
    {
        var matcher = Matcher(new AllowlistRule { Process = "Code.exe" });

        Assert.False(matcher.HasRuleFor("chrome.exe"));
        Assert.False(matcher.IsAllowed("chrome.exe", "Docs"));
    }

    [Fact]
    public void Empty_allowlist_allows_nothing()
    {
        Assert.False(Matcher().IsAllowed("Code.exe", "x"));
    }

    [Fact]
    public void Title_patterns_must_match_the_whole_title()
    {
        var matcher = Matcher(new AllowlistRule { Process = "app.exe", TitlePatterns = ["* - Project X", "Report ?"] });

        Assert.True(matcher.HasRuleFor("app.exe"));
        Assert.True(matcher.IsAllowed("app.exe", "main.cs - Project X"));
        Assert.True(matcher.IsAllowed("app.exe", "report 7"));
        Assert.False(matcher.IsAllowed("app.exe", "main.cs - Project X - extra"));
        Assert.False(matcher.IsAllowed("app.exe", "Report 12"));
        Assert.False(matcher.IsAllowed("app.exe", null));
    }

    [Fact]
    public void Pattern_characters_other_than_wildcards_are_literal()
    {
        var matcher = Matcher(new AllowlistRule { Process = "app.exe", TitlePatterns = ["a.b (c)*"] });

        Assert.True(matcher.IsAllowed("app.exe", "a.b (c) d"));
        Assert.False(matcher.IsAllowed("app.exe", "axb (c) d"));
    }

    [Fact]
    public void Any_matching_rule_for_the_process_allows_it()
    {
        var matcher = Matcher(
            new AllowlistRule { Process = "app.exe", TitlePatterns = ["Only this"] },
            new AllowlistRule { Process = "app.exe" });

        Assert.True(matcher.IsAllowed("app.exe", "Something else"));
    }

    [Theory]
    [InlineData("New tab - InPrivate - Microsoft Edge")]
    [InlineData("New Tab - Google Chrome (Incognito)")]
    [InlineData("Mozilla Firefox Private Browsing")]
    public void Private_windows_are_never_allowed(string title)
    {
        var matcher = Matcher(new AllowlistRule { Process = "browser.exe" });

        Assert.False(matcher.IsAllowed("browser.exe", title));
    }
}
