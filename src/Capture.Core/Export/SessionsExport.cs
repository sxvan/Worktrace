using System.Globalization;
using System.Text;

namespace Capture.Core.Export;

/// <summary>
/// A short summary of the day's Claude Code sessions: when each was active, in which folder and
/// branch, and what the user asked. The full transcripts are large, so this is what the model
/// reads first; each session names its transcript file for when the summary is not enough.
/// Separate from <see cref="DayExport"/>, which holds only what TimeCapture recorded.
/// </summary>
public static class SessionsExport
{
    /// <summary>At most this many prompts are listed per session.</summary>
    public const int MaxPromptsPerSession = 40;

    public static string ToText(DateOnly day, IReadOnlyList<AgentSession> sessions, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.Append(DayExport.Heading("Claude Code sessions", day, zone));
        text.Append("Active spans (pauses over 10 minutes split them), folder, branch, the prompts typed, and the transcript file.\n\n");
        if (sessions.Count == 0)
        {
            text.Append("(none)\n");
        }

        foreach (AgentSession session in sessions.OrderBy(s => s.ActiveSpans[0].Start))
        {
            text.Append("## ").Append(session.WorkingDirectory ?? "(unknown folder)");
            if (session.GitBranches.Count > 0)
            {
                text.Append(" [").AppendJoin(", ", session.GitBranches).Append(']');
            }
            text.Append('\n');
            text.Append("active: ")
                .AppendJoin(", ", session.ActiveSpans.Select(s => $"{DayExport.Minute(s.Start, zone)}-{DayExport.Minute(s.End, zone)}"))
                .Append('\n');
            if (session.TranscriptPath is { } path)
            {
                text.Append("transcript: ").Append(path).Append('\n');
            }
            foreach (AgentPrompt prompt in DayExport.Spread(session.Prompts, MaxPromptsPerSession))
            {
                text.Append(CultureInfo.InvariantCulture, $"- {DayExport.Minute(prompt.Timestamp, zone)} {prompt.Text}\n");
            }
            if (session.Prompts.Count > MaxPromptsPerSession)
            {
                text.Append(CultureInfo.InvariantCulture, $"- ({session.Prompts.Count - MaxPromptsPerSession} more prompts not shown)\n");
            }
            text.Append('\n');
        }

        return text.ToString();
    }
}
