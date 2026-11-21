using Capture.Core.Rules;
using Capture.Core.Config;

namespace Capture.Core.Tests;

public class WindowRuleMatcherTests
{
    private static WindowRuleMatcher Matcher(params WindowRule[] rules) => new(rules);

    [Theory]
    [InlineData("Code.exe")]
    [InlineData("code.EXE")]
    [InlineData("Code")]
    public void Process_match_ignores_case_and_exe_suffix(string process)
    {
        var matcher = Matcher(new WindowRule { Process = "Code.exe" });

        Assert.True(matcher.HasRuleFor(process));
        Assert.True(matcher.ShouldCapture(process, "anything"));
    }

    [Fact]
    public void Unknown_process_is_not_captured()
    {
        var matcher = Matcher(new WindowRule { Process = "Code.exe" });

        Assert.False(matcher.HasRuleFor("chrome.exe"));
        Assert.False(matcher.ShouldCapture("chrome.exe", "Docs"));
    }

    [Fact]
    public void No_rules_capture_nothing()
    {
        Assert.False(Matcher().ShouldCapture("Code.exe", "x"));
    }

    [Fact]
    public void Title_patterns_must_match_the_whole_title()
    {
        var matcher = Matcher(new WindowRule { Process = "app.exe", TitlePatterns = ["* - Project X", "Report ?"] });

        Assert.True(matcher.HasRuleFor("app.exe"));
        Assert.True(matcher.ShouldCapture("app.exe", "main.cs - Project X"));
        Assert.True(matcher.ShouldCapture("app.exe", "report 7"));
        Assert.False(matcher.ShouldCapture("app.exe", "main.cs - Project X - extra"));
        Assert.False(matcher.ShouldCapture("app.exe", "Report 12"));
        Assert.False(matcher.ShouldCapture("app.exe", null));
    }

    [Fact]
    public void Pattern_characters_other_than_wildcards_are_literal()
    {
        var matcher = Matcher(new WindowRule { Process = "app.exe", TitlePatterns = ["a.b (c)*"] });

        Assert.True(matcher.ShouldCapture("app.exe", "a.b (c) d"));
        Assert.False(matcher.ShouldCapture("app.exe", "axb (c) d"));
    }

    [Fact]
    public void Any_matching_rule_for_the_process_captures_it()
    {
        var matcher = Matcher(
            new WindowRule { Process = "app.exe", TitlePatterns = ["Only this"] },
            new WindowRule { Process = "app.exe" });

        Assert.True(matcher.ShouldCapture("app.exe", "Something else"));
    }

    [Theory]
    [InlineData("New tab - InPrivate - Microsoft Edge")]
    [InlineData("New Tab - Google Chrome (Incognito)")]
    [InlineData("Mozilla Firefox Private Browsing")]
    public void Private_windows_are_never_captured(string title)
    {
        var matcher = Matcher(new WindowRule { Process = "browser.exe" });

        Assert.False(matcher.ShouldCapture("browser.exe", title));
    }

    // ---- Modes ----

    [Fact]
    public void Capture_is_the_default_mode_and_always_logs_titles()
    {
        var matcher = Matcher(new WindowRule { Process = "Code.exe" });

        Assert.Equal(new WindowClassification(WindowMode.Capture, LogTitle: true), matcher.Classify("Code.exe", "a.cs"));
    }

    [Fact]
    public void Log_rule_records_the_app_but_is_never_captured()
    {
        var matcher = Matcher(new WindowRule { Process = "keepass.exe", Mode = "log" });

        Assert.Equal(new WindowClassification(WindowMode.Log, LogTitle: false), matcher.Classify("keepass.exe", "Bank login"));
        Assert.False(matcher.ShouldCapture("keepass.exe", "Bank login"));
        Assert.True(matcher.HasRuleFor("keepass.exe"));
    }

    [Fact]
    public void Log_rule_can_opt_in_to_titles()
    {
        var matcher = Matcher(new WindowRule { Process = "msedge.exe", Mode = "LOG", LogTitles = true });

        Assert.Equal(new WindowClassification(WindowMode.Log, LogTitle: true), matcher.Classify("msedge.exe", "Docs"));
    }

    [Fact]
    public void Process_without_a_rule_is_other()
    {
        var matcher = Matcher(new WindowRule { Process = "Code.exe" });

        Assert.Equal(WindowClassification.Other, matcher.Classify("spotify.exe", "Song"));
    }

    [Fact]
    public void Capture_rule_wins_over_a_log_rule_for_the_same_window()
    {
        var matcher = Matcher(
            new WindowRule { Process = "app.exe", Mode = "log" },
            new WindowRule { Process = "app.exe", TitlePatterns = ["Work *"] });

        Assert.Equal(WindowMode.Capture, matcher.Classify("app.exe", "Work item").Mode);
        Assert.Equal(WindowMode.Log, matcher.Classify("app.exe", "Private item").Mode);
    }

    [Fact]
    public void Private_windows_are_other_even_with_a_log_rule()
    {
        var matcher = Matcher(new WindowRule { Process = "msedge.exe", Mode = "log", LogTitles = true });

        Assert.Equal(WindowClassification.Other, matcher.Classify("msedge.exe", "New tab - InPrivate - Microsoft Edge"));
    }

    [Fact]
    public void Catch_all_rule_applies_to_every_application_without_its_own_rule()
    {
        var matcher = Matcher(new WindowRule { Process = "*" });

        Assert.True(matcher.HasRuleFor("anything.exe"));
        Assert.Equal(WindowMode.Capture, matcher.Classify("spotify.exe", "Song").Mode);
        Assert.Equal(WindowMode.Capture, matcher.Classify("Code.exe", "a.cs").Mode);
    }

    [Fact]
    public void Own_rule_of_an_application_overrides_the_catch_all()
    {
        var matcher = Matcher(
            new WindowRule { Process = "*" },
            new WindowRule { Process = "keepass.exe", Mode = "log" });

        Assert.Equal(new WindowClassification(WindowMode.Log, LogTitle: false), matcher.Classify("keepass.exe", "Bank"));
        Assert.Equal(WindowMode.Capture, matcher.Classify("notepad.exe", "notes.txt").Mode);
    }

    [Fact]
    public void Catch_all_skips_untitled_windows_and_private_windows()
    {
        var matcher = Matcher(new WindowRule { Process = "*" });

        Assert.Equal(WindowClassification.Other, matcher.Classify("helper.exe", ""));
        Assert.Equal(WindowClassification.Other, matcher.Classify("msedge.exe", "New tab - InPrivate - Microsoft Edge"));
    }

    [Fact]
    public void Rule_with_an_unknown_mode_is_ignored()
    {
        var matcher = Matcher(new WindowRule { Process = "app.exe", Mode = "everything" });

        Assert.False(matcher.HasRuleFor("app.exe"));
        Assert.Equal(WindowClassification.Other, matcher.Classify("app.exe", "x"));
    }

    [Fact]
    public void An_excluded_title_is_left_to_the_other_rules()
    {
        var matcher = Matcher(
            new WindowRule { Process = "*", ExcludeTitlePatterns = ["*YouTube*"] },
            new WindowRule { Process = "*", Mode = "log", LogTitles = true });

        Assert.Equal(new WindowClassification(WindowMode.Log, LogTitle: true),
            matcher.Classify("msedge.exe", "Lofi beats - YouTube and 7 more pages - Personal - Microsoft Edge"));
        Assert.Equal(WindowMode.Capture, matcher.Classify("msedge.exe", "sxvan/Glove - GitHub - Microsoft Edge").Mode);
    }

    [Fact]
    public void An_excluded_title_without_another_rule_is_other()
    {
        var matcher = Matcher(new WindowRule { Process = "msedge.exe", ExcludeTitlePatterns = ["*Bank*"] });

        Assert.Equal(WindowClassification.Other, matcher.Classify("msedge.exe", "My Bank - Login"));
        Assert.True(matcher.ShouldCapture("msedge.exe", "Docs"));
    }

    [Fact]
    public void Exclusion_wins_over_the_rule_s_own_title_patterns()
    {
        var matcher = Matcher(new WindowRule { Process = "chrome.exe", TitlePatterns = ["*GitHub*"], ExcludeTitlePatterns = ["*Settings*"] });

        Assert.True(matcher.ShouldCapture("chrome.exe", "Pull requests - GitHub"));
        Assert.False(matcher.ShouldCapture("chrome.exe", "Settings - GitHub"));
    }
}
