namespace RockBot.Host;

/// <summary>
/// Published by a subagent when it has completed its task.
/// </summary>
public sealed record SubagentResultMessage
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Output { get; init; }
    public required bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? BatchId { get; init; }
    public bool Consolidate { get; init; } = true;

    /// <summary>
    /// The user message that led the primary agent to spawn this subagent, when known (#666).
    /// The synthesis turn's completion check judges the relayed result against it rather than
    /// against the subagent's own task description, which the primary may have paraphrased.
    /// </summary>
    public string? OriginatingUserRequest { get; init; }

    /// <summary>
    /// The task description the subagent was spawned with (#665). Recorded with the result in the
    /// session work registry so later subagents in the conversation can see what this one did.
    /// Null on results from an older build.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The tool calls the subagent made, oldest first, compacted and capped (#683). The primary's
    /// synthesis turn makes no write calls of its own, so without these the completion check saw
    /// every honest relay of a subagent's writes and uploads as an unsupported claim. Null on
    /// results from an older build; an empty list means the subagent made no tool calls.
    /// </summary>
    public IReadOnlyList<SubagentToolCallSummary>? ToolCalls { get; init; }

    /// <summary>
    /// How many tool calls the subagent made in all (#683). Larger than <see cref="ToolCalls"/>'s
    /// count when the list was capped. Null on results from an older build.
    /// </summary>
    public int? ToolCallCount { get; init; }
}

/// <summary>One tool call a subagent made, as carried on <see cref="SubagentResultMessage.ToolCalls"/> (#683).</summary>
/// <param name="Name">The tool name as the subagent called it; generic proxies show their target, e.g. <c>mcp_invoke_tool → upload_file</c>.</param>
/// <param name="Succeeded">False when the call threw or returned an error result.</param>
/// <param name="ChangesState">True when the call changed something outside the subagent's own scratch state (wrote, uploaded, sent…).</param>
/// <param name="Arguments">A short argument summary, or null.</param>
public sealed record SubagentToolCallSummary(string Name, bool Succeeded, bool ChangesState, string? Arguments);
