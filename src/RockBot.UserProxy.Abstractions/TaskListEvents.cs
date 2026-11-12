namespace RockBot.UserProxy;

/// <summary>
/// The whole task list of a run, sent when the agent creates (or replaces) it. Modeled on AG-UI's
/// <c>STATE_SNAPSHOT</c>. The primary turn's list goes to the turn's <c>ReplyTo</c> topic; a
/// subagent's is broadcast on <see cref="UserProxyTopics.UserEvent"/>.
/// </summary>
public sealed record TaskListSnapshot
{
    public required string ThreadId { get; init; }

    /// <summary>The primary turn's correlation id, when the list belongs to one.</summary>
    public string? RunId { get; init; }

    /// <summary>Whose list it is: the primary turn, or a subagent (with its task id).</summary>
    public required ReplySource Source { get; init; }

    public required IReadOnlyList<TaskListItem> Items { get; init; }
}

/// <summary>One item of a task list changed. Modeled on AG-UI's <c>STATE_DELTA</c>.</summary>
public sealed record TaskListDelta
{
    public required string ThreadId { get; init; }
    public string? RunId { get; init; }
    public required ReplySource Source { get; init; }
    public required TaskListItem Item { get; init; }
}

/// <summary>One item of an agent's task list.</summary>
public sealed record TaskListItem
{
    public required string Id { get; init; }
    public required string Description { get; init; }

    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>skipped</c>.</summary>
    public required string Status { get; init; }
}
