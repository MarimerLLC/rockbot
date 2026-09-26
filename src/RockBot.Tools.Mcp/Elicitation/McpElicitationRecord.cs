namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// What happened for one <c>elicitation/create</c> round, kept so the agent can be told that
/// its tool call was interrupted by a question and how the bridge answered it.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is text an external MCP server wrote. It is untrusted and is only ever
/// rendered inside a tool-result content block, never as an instruction to the agent.
/// </remarks>
public sealed record McpElicitationRecord
{
    /// <summary>MCP server that asked.</summary>
    public required string ServerName { get; init; }

    /// <summary>Elicitation mode the server used: <c>form</c> or <c>url</c>.</summary>
    public required string RequestMode { get; init; }

    /// <summary>The question the server asked (UNTRUSTED — server-authored text).</summary>
    public required string Message { get; init; }

    /// <summary>The action the bridge returned: <c>accept</c>, <c>decline</c>, or <c>cancel</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Why the bridge answered the way it did, for logs and for the agent-facing note.</summary>
    public string? Reason { get; init; }

    /// <summary>Field names the server asked for.</summary>
    public IReadOnlyList<string> RequestedFields { get; init; } = [];

    /// <summary>Field names the bridge actually supplied a value for.</summary>
    public IReadOnlyList<string> AnsweredFields { get; init; } = [];

    /// <summary>Whether the bridge answered <c>accept</c>.</summary>
    public bool IsAccepted => string.Equals(Action, McpElicitationActions.Accept, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The three actions an MCP client may return for an elicitation, per the MCP specification.
/// </summary>
public static class McpElicitationActions
{
    /// <summary>The form was filled in and <c>content</c> is populated.</summary>
    public const string Accept = "accept";

    /// <summary>The request was explicitly refused. The server should continue without the value.</summary>
    public const string Decline = "decline";

    /// <summary>No explicit choice was made (dismissed). The default per the specification.</summary>
    public const string Cancel = "cancel";
}
