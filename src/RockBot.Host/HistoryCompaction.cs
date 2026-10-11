namespace RockBot.Host;

/// <summary>
/// Shortens conversation-history turns as they are replayed into a request (#670). Storage is
/// never touched: the full turn stays in conversation memory, and the cut is a pure function of
/// the turn's position, so the same turn renders the same way on every request between moves.
/// </summary>
public static class HistoryCompaction
{
    /// <summary>Share of the kept characters that come from the start of the text; the rest come from the end.</summary>
    private const double HeadRatio = 0.7;

    /// <summary>Working-memory key where the full output of a long subagent result is kept for later turns.</summary>
    public static string SubagentOutputKey(string taskId) => $"subagent/{taskId}/output";

    /// <summary>
    /// The task id of a subagent relay turn's <c>AgentName</c> (<c>subagent-{taskId}</c>), or null
    /// when the turn is not a subagent relay.
    /// </summary>
    public static string? SubagentTaskId(string? agentName) =>
        agentName is not null && agentName.StartsWith("subagent-", StringComparison.Ordinal) && agentName.Length > 9
            ? agentName[9..]
            : null;

    /// <summary>
    /// Keeps the start and end of <paramref name="text"/> within <paramref name="maxChars"/> and puts
    /// <paramref name="marker"/> (with <c>{0}</c> replaced by the number of characters removed)
    /// between them. Text that already fits is returned unchanged; a non-positive limit disables the cut.
    /// </summary>
    public static string HeadTail(string text, int maxChars, string marker)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
            return text;

        var head = (int)(maxChars * HeadRatio);
        var tail = maxChars - head;
        var removed = text.Length - head - tail;
        var note = string.Format(System.Globalization.CultureInfo.InvariantCulture, marker, removed);
        return $"{text.AsSpan(0, head).TrimEnd()}\n[{note}]\n{text.AsSpan(text.Length - tail).TrimStart()}";
    }

    /// <summary>
    /// Renders a subagent result that the primary has already relayed to the user: its start and
    /// end, and where the full text is kept.
    /// </summary>
    public static string RelayedSubagentTurn(string content, string taskId, int maxChars) =>
        HeadTail(content, maxChars,
            "… {0} chars of this subagent result elided — it was already relayed to the user. " +
            $"If you need the full text, it is in working memory at '{SubagentOutputKey(taskId)}' while that entry lasts …");

    /// <summary>Renders an older history turn: its start and end.</summary>
    public static string OlderTurn(string content, int maxChars) =>
        HeadTail(content, maxChars, "… {0} chars of this earlier turn elided …");
}
