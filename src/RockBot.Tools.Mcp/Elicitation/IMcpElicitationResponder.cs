using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Produces the answer to an MCP server's <c>elicitation/create</c> request.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="McpElicitationCoordinator"/> so the decision of <em>who answers</em>
/// is a deployment choice rather than a hard-coded one. The shipped implementation is
/// <see cref="LlmElicitationResponder"/>, which answers from the in-flight tool call's own
/// arguments; a deployment that can reach a person in the seconds a tool call has left can
/// register a responder that asks them instead.
/// </para>
/// <para>
/// A responder's answer is never trusted: the coordinator validates it against the server's
/// requested schema before any of it reaches the server.
/// </para>
/// </remarks>
public interface IMcpElicitationResponder
{
    /// <summary>
    /// Answers one elicitation request, or declines it.
    /// </summary>
    /// <param name="context">The request plus whatever the bridge knows about the call it interrupted.</param>
    /// <param name="ct">Bounded by <see cref="McpElicitationConfig.ResponderTimeoutMs"/>.</param>
    ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct);

    /// <summary>
    /// Whether this responder may only answer for a server whose <em>own</em> policy names it.
    /// </summary>
    /// <remarks>
    /// A responder that draws on anything beyond the call's own arguments (the conversation, for
    /// instance) hands that data to whichever server asks. Naming it in the bridge-wide default
    /// would extend it to every server the model registers at runtime — including one at a URL the
    /// model chose — so <see cref="McpElicitationResponders.Resolve"/> refuses it there.
    /// </remarks>
    bool RequiresServerOptIn => false;
}

/// <summary>
/// Everything a responder is given about an elicitation.
/// </summary>
/// <param name="ServerName">The MCP server that asked.</param>
/// <param name="Request">The raw request, including the message and requested schema (UNTRUSTED).</param>
/// <param name="InFlightCalls">
/// The tool call(s) in flight against this server when the question arrived. MCP gives no link
/// from an elicitation back to the call that triggered it, so when more than one call is open
/// the bridge cannot say which one asked and passes them all.
/// </param>
/// <param name="KnownValues">
/// Fields already settled from the server's configured <see cref="McpElicitationConfig.Defaults"/>.
/// A responder should fill in the rest and leave these alone — operator configuration outranks it,
/// and the coordinator overwrites any of these it tries to change.
/// </param>
public sealed record McpElicitationContext(
    string ServerName,
    ElicitRequestParams Request,
    IReadOnlyList<McpElicitationCallContext> InFlightCalls,
    IReadOnlyDictionary<string, JsonElement> KnownValues);

/// <summary>
/// An in-flight tool call, as context for answering.
/// </summary>
/// <param name="ToolName">Tool being invoked.</param>
/// <param name="Arguments">The raw JSON arguments the agent supplied, or null when there were none.</param>
/// <param name="SessionId">
/// The agent session that made the call, when the caller supplied one. A responder that hands the
/// question back to the agent (rather than answering from <paramref name="Arguments"/>) needs it
/// to know which conversation to ask in.
/// </param>
public sealed record McpElicitationCallContext(string ToolName, string? Arguments, string? SessionId = null);

/// <summary>
/// A responder's proposed answer. Still subject to schema validation by the coordinator.
/// </summary>
/// <param name="Accepted">True to fill the form in; false to decline.</param>
/// <param name="Content">Proposed field values, keyed by the server's field names.</param>
/// <param name="Reason">Why, when declining — surfaced to the agent so it can supply the value itself.</param>
public sealed record McpElicitationAnswer(
    bool Accepted,
    IReadOnlyDictionary<string, JsonElement>? Content,
    string? Reason)
{
    /// <summary>A decline with the given reason.</summary>
    public static McpElicitationAnswer Decline(string reason) => new(false, null, reason);

    /// <summary>An acceptance carrying the given field values.</summary>
    public static McpElicitationAnswer Accept(IReadOnlyDictionary<string, JsonElement> content) => new(true, content, null);
}
