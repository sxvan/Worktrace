using Capture.Core.Export;

namespace Capture.Core.Tests;

public class DayExportTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static string Export(IReadOnlyList<Visit> visits, IReadOnlyList<ScreenshotRef> shots) =>
        DayExport.ToText(Day, visits, shots, TimeZoneInfo.Utc);

    [Fact]
    public void Screenshots_are_listed_with_the_visit_of_their_window()
    {
        Visit[] visits =
        [
            new(At(0), At(10), VisitKind.Window, "Code.exe", "Program.cs"),
            new(At(10), At(15), VisitKind.Idle),
            new(At(15), At(20), VisitKind.Window, "msedge.exe", "Docs"),
        ];
        ScreenshotRef[] shots = [new(At(1), 11, "Code.exe"), new(At(9), 12, "Code.exe"), new(At(16), 13, "msedge.exe")];

        string[] lines = Export(visits, shots).Split('\n');

        Assert.Contains(lines, l => l.StartsWith("09:00:00-09:10:00") && l.Contains("Code.exe \"Program.cs\"") && l.EndsWith("shots: 11@09:01 12@09:09"));
        Assert.Contains(lines, l => l.StartsWith("09:10:00-09:15:00") && l.Contains("(idle)") && !l.Contains("shots:"));
        Assert.Contains(lines, l => l.StartsWith("09:15:00-09:20:00") && l.EndsWith("shots: 13@09:16"));
    }

    [Fact]
    public void A_screenshot_of_another_process_is_not_attached()
    {
        Visit[] visits = [new(At(0), At(10), VisitKind.Window, "Code.exe", "A")];

        Assert.DoesNotContain("shots:", Export(visits, [new ScreenshotRef(At(1), 1, "other.exe")]));
    }

    [Fact]
    public void Long_visits_list_a_spread_of_screenshots()
    {
        Visit[] visits = [new(At(0), At(60), VisitKind.Window, "Code.exe", "A")];
        ScreenshotRef[] shots = Enumerable.Range(0, 30).Select(i => new ScreenshotRef(At(i * 2), 100 + i, "Code.exe")).ToArray();

        string line = Export(visits, shots).Split('\n').Single(l => l.Contains("shots:"));

        Assert.Contains(" 100@09:00", line);
        Assert.Contains(" 129@09:58", line);
        Assert.EndsWith("(30 in all)", line);
        Assert.Equal(DayExport.MaxScreenshotsPerVisit, line.Split('@').Length - 1);
    }

    [Fact]
    public void The_day_export_has_no_sessions()
    {
        Assert.DoesNotContain("Claude Code", Export([], []));
    }

    [Fact]
    public void Sessions_show_folder_branch_spans_prompts_and_transcript()
    {
        AgentSession session = new("s1", @"C:\repo", ["feat/x"],
            [new ActiveSpan(At(0), At(30)), new ActiveSpan(At(60), At(70))],
            [new AgentPrompt(At(1), "add the export")]);

        string text = SessionsExport.ToText(Day, [session with { TranscriptPath = @"C:\t\s1.jsonl" }], TimeZoneInfo.Utc);

        Assert.Contains("## C:\\repo [feat/x]", text);
        Assert.Contains(@"transcript: C:\t\s1.jsonl", text);
        Assert.Contains("active: 09:00-09:30, 10:00-10:10", text);
        Assert.Contains("- 09:01 add the export", text);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(125, "2m05s")]
    [InlineData(3900, "1h05m")]
    public void Durations_are_short(int seconds, string expected)
    {
        Assert.Equal(expected, DayExport.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
