using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Hands a server's question to the agent that made the call and waits for its answer
/// (<see cref="McpElicitationConfig.ModeHandback"/>). The bridge gives one to a call's
/// <see cref="McpElicitationCallScope"/> only when the call can take a hand-back: the server is in
/// hand-back mode and speaks the 2026-07-28 protocol, and the caller can answer with
/// <c>mcp_answer</c>.
/// </summary>
/// <remarks>
/// The coordinator has already run every in-band check by the time it asks: credentials, denied
/// and unreadable fields, url mode, the per-call cap, and defaults that settle the whole form.
/// What reaches the channel is a question the operator wants the agent to own.
/// </remarks>
public interface IMcpHandbackChannel
{
    /// <summary>
    /// Hands <paramref name="question"/> back and waits, possibly for many minutes, for the agent's
    /// answer. Cancelled when the call is (it expired, its server was removed, or the bridge is
    /// stopping).
    /// </summary>
    ValueTask<McpHandbackOutcome> AskAsync(McpHandbackQuestion question, CancellationToken ct);
}

/// <summary>A question to hand back: what the server sent, and which round of the call it is.</summary>
/// <param name="ServerName">The server asking.</param>
/// <param name="Request">The server's question and form, as it sent them.</param>
/// <param name="Round">1 for the call's first question, 2 for the next, and so on.</param>
public sealed record McpHandbackQuestion(string ServerName, ElicitRequestParams Request, int Round);

/// <summary>What became of a handed-back question.</summary>
/// <param name="Action">One of <see cref="McpElicitationActions"/>.</param>
/// <param name="Content">The answer's field values, for an accept.</param>
/// <param name="Reason">Why it wasn't accepted, for the record and the agent.</param>
public sealed record McpHandbackOutcome(
    string Action,
    IReadOnlyDictionary<string, JsonElement>? Content = null,
    string? Reason = null)
{
    public static McpHandbackOutcome Accept(IReadOnlyDictionary<string, JsonElement> content) =>
        new(McpElicitationActions.Accept, content);

    public static McpHandbackOutcome Decline(string reason) =>
        new(McpElicitationActions.Decline, null, reason);

    public static McpHandbackOutcome Cancel(string reason) =>
        new(McpElicitationActions.Cancel, null, reason);
}
