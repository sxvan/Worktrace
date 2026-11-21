using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capture.Core.Export;

public sealed record AgentPrompt(DateTimeOffset Timestamp, string Text);

/// <summary>A stretch in which a session wrote entries without a longer pause.</summary>
public sealed record ActiveSpan(DateTimeOffset Start, DateTimeOffset End);

/// <summary>What one Claude Code session did within the export range.</summary>
public sealed record AgentSession(
    string SessionId,
    string? WorkingDirectory,
    IReadOnlyList<string> GitBranches,
    IReadOnlyList<ActiveSpan> ActiveSpans,
    IReadOnlyList<AgentPrompt> Prompts);

/// <summary>
/// Reads a Claude Code transcript (one JSON object per line) and keeps what helps a timesheet:
/// when the session was active, in which folder and branch, and what the user asked. Assistant
/// output and tool results are left out.
/// </summary>
public static class TranscriptReader
{
    /// <summary>Entries further apart than this start a new active span.</summary>
    public static readonly TimeSpan SpanGap = TimeSpan.FromMinutes(10);

    public const int MaxPromptLength = 240;

    /// <summary>The session's activity within [from, to), or null if it has none there.</summary>
    public static AgentSession? Read(IEnumerable<string> jsonLines, DateTimeOffset from, DateTimeOffset to)
    {
        string? sessionId = null, cwd = null;
        var branches = new List<string>();
        var times = new List<DateTimeOffset>();
        var prompts = new List<AgentPrompt>();

        foreach (string json in jsonLines)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue; // A line being written while we read, or a format we do not know.
            }

            using (document)
            {
                JsonElement entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object
                    || String(entry, "type") is not ("user" or "assistant")
                    || Bool(entry, "isSidechain")
                    || String(entry, "timestamp") is not { } stamp
                    || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp)
                    || timestamp < from || timestamp >= to)
                {
                    continue;
                }

                times.Add(timestamp);
                sessionId ??= String(entry, "sessionId");
                cwd = String(entry, "cwd") ?? cwd;
                if (String(entry, "gitBranch") is { Length: > 0 } branch && !branches.Contains(branch))
                {
                    branches.Add(branch);
                }

                if (String(entry, "type") == "user" && !Bool(entry, "isMeta") && PromptText(entry) is { } prompt)
                {
                    prompts.Add(new AgentPrompt(timestamp, prompt));
                }
            }
        }

        if (times.Count == 0)
        {
            return null;
        }
        return new AgentSession(sessionId ?? "", cwd, branches, Spans(times), prompts.OrderBy(p => p.Timestamp).ToList());
    }

    internal static IReadOnlyList<ActiveSpan> Spans(List<DateTimeOffset> times)
    {
        times.Sort();
        var spans = new List<ActiveSpan>();
        DateTimeOffset start = times[0], end = times[0];
        foreach (DateTimeOffset time in times.Skip(1))
        {
            if (time - end > SpanGap)
            {
                spans.Add(new ActiveSpan(start, end));
                start = time;
            }
            end = time;
        }
        spans.Add(new ActiveSpan(start, end));
        return spans;
    }

    /// <summary>
    /// The text the user typed, shortened, or null for entries that are not a prompt: tool
    /// results, and messages the client inserts itself (they start with a tag such as &lt;command-name&gt;).
    /// </summary>
    private static string? PromptText(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        string? text = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (JsonElement block in content.EnumerateArray())
            {
                string? blockType = block.ValueKind == JsonValueKind.Object ? String(block, "type") : null;
                if (blockType == "tool_result")
                {
                    return null;
                }
                if (blockType == "text" && String(block, "text") is { } part)
                {
                    builder.Append(part).Append(' ');
                }
            }
            text = builder.ToString();
        }

        text = Collapse(text);
        if (text.Length == 0 || text.StartsWith('<'))
        {
            return null;
        }
        return text.Length <= MaxPromptLength ? text : text[..(MaxPromptLength - 1)] + "…";
    }

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
