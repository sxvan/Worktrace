using System.Text.Json;
using Capture.Core.Rules;
using Capture.Core.Config;

namespace Capture.Core.Tests;

public class WindowRuleMatcherTests
{
    private static WindowRuleMatcher Matcher(params WindowRule[] include) => new(include);

    private static WindowRuleMatcher Matcher(WindowRule[] include, params WindowRule[] exclude) => new(include, exclude);

    private static readonly WindowClassification Capture = new(WindowMode.Capture, LogTitle: true);
    private static readonly WindowClassification LogWithTitle = new(WindowMode.Log, LogTitle: true);
    private static readonly WindowClassification LogOnly = new(WindowMode.Log, LogTitle: false);

    // ---- Include rules ----

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
        Assert.Equal(WindowClassification.Other, matcher.Classify("chrome.exe", "Docs"));
    }

    [Fact]
    public void No_rules_capture_nothing()
    {
        Assert.False(Matcher().ShouldCapture("Code.exe", "x"));
        Assert.False(Matcher().HasRuleFor("Code.exe"));
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

    [Theory]
    [InlineData(null)]
    [InlineData("capture")]
    [InlineData("CAPTURE")]
    public void Capture_is_the_default_mode_and_logs_titles(string? mode)
    {
        var matcher = Matcher(new WindowRule { Process = "Code.exe", Mode = mode! });

        Assert.Equal(Capture, matcher.Classify("Code.exe", "a.cs"));
    }

    [Fact]
    public void Log_with_titles_records_app_and_title_but_never_captures()
    {
        var matcher = Matcher(new WindowRule { Process = "msedge.exe", Mode = "logWithTitles" });

        Assert.Equal(LogWithTitle, matcher.Classify("msedge.exe", "Docs"));
        Assert.False(matcher.ShouldCapture("msedge.exe", "Docs"));
    }

    [Fact]
    public void Log_records_the_app_only()
    {
        var matcher = Matcher(new WindowRule { Process = "keepass.exe", Mode = "log" });

        Assert.Equal(LogOnly, matcher.Classify("keepass.exe", "Bank login"));
        Assert.True(matcher.HasRuleFor("keepass.exe"));
    }

    [Fact]
    public void The_strongest_matching_include_rule_wins()
    {
        var matcher = Matcher(
            new WindowRule { Process = "app.exe", Mode = "log" },
            new WindowRule { Process = "app.exe", Mode = "logWithTitles", TitlePatterns = ["Notes *"] },
            new WindowRule { Process = "app.exe", TitlePatterns = ["Work *"] });

        Assert.Equal(Capture, matcher.Classify("app.exe", "Work item"));
        Assert.Equal(LogWithTitle, matcher.Classify("app.exe", "Notes item"));
        Assert.Equal(LogOnly, matcher.Classify("app.exe", "Private item"));
    }

    [Fact]
    public void Catch_all_and_own_rules_add_up()
    {
        var matcher = Matcher(
            new WindowRule { Process = "*", Mode = "log" },
            new WindowRule { Process = "Code.exe" });

        Assert.True(matcher.HasRuleFor("anything.exe"));
        Assert.Equal(LogOnly, matcher.Classify("spotify.exe", "Song"));
        Assert.Equal(Capture, matcher.Classify("Code.exe", "a.cs"));
    }

    [Fact]
    public void Catch_all_skips_untitled_windows()
    {
        var matcher = Matcher(new WindowRule { Process = "*" });

        Assert.Equal(WindowClassification.Other, matcher.Classify("helper.exe", ""));
        Assert.Equal(Capture, matcher.Classify("helper.exe", "x"));
    }

    [Theory]
    [InlineData("everything")]
    [InlineData("logTitles")]
    public void Rule_with_an_unknown_mode_is_ignored(string mode)
    {
        var matcher = Matcher(new WindowRule { Process = "app.exe", Mode = mode });

        Assert.False(matcher.HasRuleFor("app.exe"));
        Assert.Equal(WindowClassification.Other, matcher.Classify("app.exe", "x"));
    }

    [Theory]
    [InlineData("New tab - InPrivate - Microsoft Edge")]
    [InlineData("New Tab - Google Chrome (Incognito)")]
    [InlineData("Mozilla Firefox Private Browsing")]
    public void Private_windows_are_never_recorded(string title)
    {
        var matcher = Matcher(new WindowRule { Process = "*" }, new WindowRule { Process = "browser.exe", Mode = "logWithTitles" });

        Assert.Equal(WindowClassification.Other, matcher.Classify("browser.exe", title));
    }

    // ---- Exclude rules ----

    [Fact]
    public void Exclude_capture_keeps_app_and_title()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }],
            new WindowRule { Process = "*", TitlePatterns = ["*YouTube*"] });

        Assert.Equal(LogWithTitle, matcher.Classify("msedge.exe", "Lofi beats - YouTube and 7 more pages - Personal - Microsoft Edge"));
        Assert.Equal(Capture, matcher.Classify("msedge.exe", "sxvan/Glove - GitHub - Microsoft Edge"));
    }

    [Fact]
    public void Exclude_log_with_titles_keeps_the_app_only()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }],
            new WindowRule { Process = "keepass.exe", Mode = "logWithTitles" });

        Assert.Equal(LogOnly, matcher.Classify("keepass.exe", "Bank"));
        Assert.True(matcher.HasRuleFor("keepass.exe"));
        Assert.Equal(Capture, matcher.Classify("notepad.exe", "notes.txt"));
    }

    [Fact]
    public void Exclude_log_records_nothing()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }],
            new WindowRule { Process = "*", Mode = "log", TitlePatterns = ["*Bank*"] });

        Assert.Equal(WindowClassification.Other, matcher.Classify("msedge.exe", "My Bank - Login"));
        Assert.True(matcher.ShouldCapture("msedge.exe", "Docs"));
    }

    [Fact]
    public void Exclude_wins_over_any_include_rule()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }, new WindowRule { Process = "chrome.exe", TitlePatterns = ["*GitHub*"] }],
            new WindowRule { Process = "chrome.exe", Mode = "log", TitlePatterns = ["*Settings*"] });

        Assert.True(matcher.ShouldCapture("chrome.exe", "Pull requests - GitHub"));
        Assert.Equal(WindowClassification.Other, matcher.Classify("chrome.exe", "Settings - GitHub"));
    }

    [Fact]
    public void The_strictest_matching_exclude_applies()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }],
            new WindowRule { Process = "*", TitlePatterns = ["*Mail*"] },
            new WindowRule { Process = "*", Mode = "logWithTitles", TitlePatterns = ["*Mail*"] });

        Assert.Equal(LogOnly, matcher.Classify("outlook.exe", "Inbox - Mail"));
    }

    [Fact]
    public void Exclude_does_not_lower_a_weaker_include_further()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "app.exe", Mode = "log" }],
            new WindowRule { Process = "app.exe" });

        Assert.Equal(LogOnly, matcher.Classify("app.exe", "x"));
    }

    [Fact]
    public void Exclude_alone_records_nothing()
    {
        var matcher = Matcher([], new WindowRule { Process = "*", TitlePatterns = ["*YouTube*"] });

        Assert.Equal(WindowClassification.Other, matcher.Classify("msedge.exe", "Docs"));
        Assert.False(matcher.HasRuleFor("msedge.exe"));
    }

    [Fact]
    public void Exclude_log_for_a_whole_process_means_no_rule_for_it()
    {
        var matcher = Matcher(
            [new WindowRule { Process = "*" }],
            new WindowRule { Process = "keepass.exe", Mode = "log" });

        Assert.False(matcher.HasRuleFor("keepass.exe"));
        Assert.Equal(WindowClassification.Other, matcher.Classify("keepass.exe", "Bank"));
        Assert.True(matcher.HasRuleFor("notepad.exe"));
    }

    // ---- Config ----

    [Fact]
    public void Config_warns_about_the_old_rule_format()
    {
        const string json = """
            {
              "rules": [{ "process": "*" }],
              "include": [{ "process": "msedge.exe", "mode": "log", "logTitles": true }],
              "exclude": [{ "process": "*", "mode": "nothing" }]
            }
            """;
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            CaptureConfig config = ConfigLoader.Load(path);

            List<string> warnings = config.RuleWarnings().ToList();
            Assert.Equal(3, warnings.Count);
            Assert.Contains(warnings, w => w.Contains("\"rules\""));
            Assert.Contains(warnings, w => w.Contains("\"logTitles\""));
            Assert.Contains(warnings, w => w.Contains("\"nothing\""));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Config_without_old_keys_has_no_warnings()
    {
        var config = JsonSerializer.Deserialize<CaptureConfig>(
            """{ "include": [{ "process": "*", "mode": "logWithTitles" }], "exclude": [] }""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.Empty(config.RuleWarnings());
        Assert.Equal(LogWithTitle, new WindowRuleMatcher(config).Classify("x.exe", "t"));
    }
}
