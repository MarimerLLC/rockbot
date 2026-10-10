using System.Text;

namespace RockBot.Host;

/// <summary>
/// Renders a <see cref="SessionWorkSnapshot"/> as the primary agent's compact "Work products in this
/// conversation" context section (#665), so "the doc" or "the deck" resolves to a concrete path or
/// working-memory key instead of a guess.
/// </summary>
public static class SessionWorkContext
{
    /// <summary>Default ceiling for the primary's section, in characters.</summary>
    public const int DefaultMaxChars = 1_500;

    internal const string Heading =
        "Work products in this conversation (use these exact paths and keys when the user refers to " +
        "\"the doc\", \"the deck\", \"the research\" and the like):";

    /// <summary>
    /// The section text, or null when the conversation has produced nothing. Never longer than
    /// <paramref name="maxChars"/>; what does not fit is counted in a closing line.
    /// </summary>
    public static string? RenderForPrimary(SessionWorkSnapshot snapshot, int maxChars = DefaultMaxChars)
    {
        if (snapshot.IsEmpty) return null;

        var lines = new List<(string? Header, string Line)>();
        foreach (var a in snapshot.Artifacts)
            lines.Add(("Files:", "- " + DescribeArtifact(a, snapshot.SessionId)));
        foreach (var r in snapshot.Results)
            lines.Add(("Subagent results (read with get_from_working_memory):", "- " + DescribeResult(r)));

        var sb = new StringBuilder(Heading);
        string? currentHeader = null;
        var shown = 0;
        foreach (var (header, line) in lines)
        {
            var addition = (header != currentHeader ? "\n" + header : string.Empty) + "\n" + line;
            // Leave room for the "+N more" line.
            if (sb.Length + addition.Length > maxChars - 48)
                break;
            sb.Append(addition);
            currentHeader = header;
            shown++;
        }

        if (shown == 0) return null;
        if (shown < lines.Count)
            sb.Append($"\n(+{lines.Count - shown} more not shown — search_working_memory / file_list to find them)");

        var text = sb.ToString();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    /// <summary>"path — written by X (tool), uploaded to Y".</summary>
    public static string DescribeArtifact(SessionArtifact a, string rootSessionId)
    {
        var writer = WriterLabel(a.LastWriterSessionId, rootSessionId);
        if (a.IsRemoteOnly)
            return $"{a.Path} — uploaded by {writer} ({a.LastTool})";
        var text = $"{a.Path} — last written by {writer} ({a.LastTool})";
        if (a.RemoteTarget is { Length: > 0 } remote)
            text += $", uploaded to {remote}";
        return text;
    }

    /// <summary>"key(s) — one-line description".</summary>
    public static string DescribeResult(SubagentWorkResult r)
    {
        var description = OneLine(r.Description, 110);
        var status = r.IsSuccess ? string.Empty : " [failed]";
        if (r.Keys.Count == 0)
            return $"task {r.TaskId}{status}: {description} (result is in the conversation; no saved keys)";
        var keys = string.Join(", ", r.Keys.Take(2));
        if (r.Keys.Count > 2) keys += $" (+{r.Keys.Count - 2} more)";
        return $"{keys}{status} — {description}";
    }

    /// <summary>"the primary agent" for the conversation's own session, the writer's session id otherwise.</summary>
    public static string WriterLabel(string writerSessionId, string rootSessionId) =>
        string.Equals(writerSessionId, rootSessionId, StringComparison.Ordinal) ? "the primary agent" : writerSessionId;

    public static string OneLine(string text, int max)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return SessionWorkRegistry.Truncate(flat, max);
    }
}
