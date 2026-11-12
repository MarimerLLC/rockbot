namespace RockBot.UserProxy;

/// <summary>
/// Which part of the agent produced an <see cref="AgentReply"/> or event, so a frontend doesn't
/// have to guess from agent-name prefixes or session ids.
/// </summary>
public sealed record ReplySource
{
    /// <summary>One of <see cref="ReplySourceKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The subagent's or worker's task id, for <see cref="ReplySourceKind.Subagent"/> and <see cref="ReplySourceKind.Worker"/>.</summary>
    public string? TaskId { get; init; }

    public static ReplySource Primary { get; } = new() { Kind = ReplySourceKind.Primary };
    public static ReplySource A2A { get; } = new() { Kind = ReplySourceKind.A2A };
    public static ReplySource Scheduled { get; } = new() { Kind = ReplySourceKind.Scheduled };
    public static ReplySource Bridge { get; } = new() { Kind = ReplySourceKind.Bridge };
    public static ReplySource Subagent(string taskId) => new() { Kind = ReplySourceKind.Subagent, TaskId = taskId };
    public static ReplySource Worker(string taskId) => new() { Kind = ReplySourceKind.Worker, TaskId = taskId };
}

/// <summary>Kinds a <see cref="ReplySource"/> names.</summary>
public static class ReplySourceKind
{
    /// <summary>The agent's own turn for the user.</summary>
    public const string Primary = "primary";
    public const string Subagent = "subagent";
    public const string Worker = "worker";

    /// <summary>A remote agent reached over A2A, or an inbound notification.</summary>
    public const string A2A = "a2a";

    /// <summary>A scheduled task or a background system job.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The MCP bridge (forms, restart notices).</summary>
    public const string Bridge = "bridge";
}
