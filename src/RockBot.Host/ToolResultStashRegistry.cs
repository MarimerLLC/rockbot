namespace RockBot.Host;

/// <summary>
/// Per-<see cref="AgentLoopRunner.RunAsync"/> registry of tool results that were
/// overflow-trimmed from the visible context and stashed in working memory.
/// Used by the loop to render a system-authored "stash registry" message each
/// iteration that lists every elided result with the working-memory key the
/// model can use to retrieve the full original via <c>GetFromWorkingMemory</c>.
/// <para>
/// The registry is the trusted side of the trust boundary for elided-result
/// recovery: tool output stays inert and the model is told (via directives) to
/// only retrieve based on keys that appear in this system message.
/// </para>
/// </summary>
internal sealed class ToolResultStashRegistry
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>
    /// A single stashed tool result.
    /// </summary>
    /// <param name="CallId">The function call id that produced the original result.</param>
    /// <param name="ToolName">Tool name that produced the result.</param>
    /// <param name="ArgsSummary">Short summary of the call arguments for display.</param>
    /// <param name="Key">Working-memory key under which the full original is stored.</param>
    public sealed record Entry(string CallId, string ToolName, string ArgsSummary, string Key);

    public bool IsEmpty
    {
        get { lock (_gate) return _entries.Count == 0; }
    }

    /// <summary>True if an entry with <paramref name="callId"/> is already registered.</summary>
    public bool Contains(string callId)
    {
        if (string.IsNullOrEmpty(callId)) return false;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (string.Equals(e.CallId, callId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Adds <paramref name="entry"/> if no entry with the same <see cref="Entry.CallId"/>
    /// is already registered. Idempotent — duplicate adds are silently ignored.
    /// </summary>
    public void Add(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.CallId)) return;
        lock (_gate)
        {
            foreach (var existing in _entries)
            {
                if (string.Equals(existing.CallId, entry.CallId, StringComparison.Ordinal))
                    return;
            }
            _entries.Add(entry);
        }
    }

    /// <summary>Returns a point-in-time snapshot of the registry entries.</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
