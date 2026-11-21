using System.Text.Json;
using Capture.Core.Export;

namespace Capture.Core.Tests;

public class TranscriptReaderTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(1);

    private static string Entry(string type, string time, object? content = null, bool sidechain = false, bool meta = false, string branch = "main") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = $"2026-11-20T{time}:00.000Z",
            ["sessionId"] = "s1",
            ["cwd"] = @"C:\repo",
            ["gitBranch"] = branch,
            ["isSidechain"] = sidechain,
            ["isMeta"] = meta,
            ["message"] = new { role = type, content },
        });

    [Fact]
    public void Prompts_are_kept_and_tool_results_and_inserted_messages_are_not()
    {
        string[] lines =
        [
            Entry("user", "09:00", "add an export command"),
            Entry("assistant", "09:01", new object[] { new { type = "text", text = "ok" } }),
            Entry("user", "09:02", new object[] { new { type = "tool_result", content = "file contents" } }),
            Entry("user", "09:03", "<command-name>/clear</command-name>"),
            Entry("user", "09:04", "meta text", meta: true),
            Entry("user", "09:05", new object[] { new { type = "text", text = "now   the\ntests" } }),
            Entry("user", "09:06", "a subagent's task", sidechain: true),
        ];

        AgentSession session = TranscriptReader.Read(lines, From, To)!;

        Assert.Equal(["add an export command", "now the tests"], session.Prompts.Select(p => p.Text));
        Assert.Equal(@"C:\repo", session.WorkingDirectory);
        Assert.Equal(["main"], session.GitBranches);
    }

    [Fact]
    public void Pauses_longer_than_ten_minutes_split_the_activity()
    {
        string[] lines = [Entry("user", "09:00", "a"), Entry("assistant", "09:08"), Entry("user", "09:30", "b"), Entry("assistant", "09:31")];

        AgentSession session = TranscriptReader.Read(lines, From, To)!;

        Assert.Equal([(9, 0, 9, 8), (9, 30, 9, 31)],
            session.ActiveSpans.Select(s => (s.Start.Hour, s.Start.Minute, s.End.Hour, s.End.Minute)));
    }

    [Fact]
    public void Entries_outside_the_range_and_broken_lines_are_ignored()
    {
        string[] lines = ["{not json", Entry("user", "09:00", "inside")];

        Assert.Single(TranscriptReader.Read(lines, From, To)!.Prompts);
        Assert.Null(TranscriptReader.Read(lines, To, To.AddDays(1)));
    }

    [Fact]
    public void Long_prompts_are_shortened()
    {
        AgentSession session = TranscriptReader.Read([Entry("user", "09:00", new string('x', 1000))], From, To)!;

        Assert.Equal(TranscriptReader.MaxPromptLength, session.Prompts[0].Text.Length);
    }

    [Fact]
    public void Branches_are_collected_in_order()
    {
        string[] lines = [Entry("user", "09:00", "a", branch: "main"), Entry("user", "09:01", "b", branch: "feat/x"), Entry("user", "09:02", "c", branch: "main")];

        Assert.Equal(["main", "feat/x"], TranscriptReader.Read(lines, From, To)!.GitBranches);
    }
}
