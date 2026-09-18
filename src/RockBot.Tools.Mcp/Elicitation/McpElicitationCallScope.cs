namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Marks a tool call as in flight against one MCP server so elicitations arriving while it runs
/// can be attributed to it, counted against the per-call cap, and reported back to the agent.
/// </summary>
/// <remarks>
/// MCP carries no link from an <c>elicitation/create</c> request back to the request that
/// provoked it, and the SDK dispatches server-initiated requests on the session's own message
/// loop rather than on the caller's async context, so neither the request id nor an
/// <c>AsyncLocal</c> can tie the two together. Tracking open calls per server is the honest
/// approximation: exact while one call is open, and explicit about the ambiguity when several are.
/// </remarks>
public sealed class McpElicitationCallScope : IDisposable
{
    private readonly Action<McpElicitationCallScope> _onDispose;
    private readonly List<McpElicitationRecord> _records = [];
    private readonly object _gate = new();
    private int _attempts;

    internal McpElicitationCallScope(string toolName, string? arguments, Action<McpElicitationCallScope> onDispose)
    {
        ToolName = toolName;
        Arguments = arguments;
        _onDispose = onDispose;
    }

    /// <summary>Tool being invoked.</summary>
    public string ToolName { get; }

    /// <summary>Raw JSON arguments the agent supplied, or null.</summary>
    public string? Arguments { get; }

    /// <summary>How many elicitations have been attributed to this call.</summary>
    public int Attempts
    {
        get { lock (_gate) return _attempts; }
    }

    /// <summary>
    /// What the bridge was asked and how it answered, in arrival order. Empty for the
    /// overwhelming majority of calls, which is why the agent-facing note is only added when
    /// this is not.
    /// </summary>
    public IReadOnlyList<McpElicitationRecord> Records
    {
        get { lock (_gate) return [.. _records]; }
    }

    /// <summary>Whether any elicitation happened during this call.</summary>
    public bool HasRecords
    {
        get { lock (_gate) return _records.Count > 0; }
    }

    internal void CountAttempt()
    {
        lock (_gate) _attempts++;
    }

    internal void Record(McpElicitationRecord record)
    {
        lock (_gate) _records.Add(record);
    }

    /// <inheritdoc />
    public void Dispose() => _onDispose(this);
}
