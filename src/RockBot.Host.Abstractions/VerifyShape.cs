using System.Text.Json;

namespace RockBot.Host;

/// <summary>
/// Structured predicate that lets a <see cref="CapabilityClaim"/> be falsified by re-issuing
/// the call. Stored alongside the claim's memory entry; evaluated by the read-side verifier
/// before the entry is injected into a session prompt.
/// </summary>
/// <param name="Server">MCP server name to invoke (e.g. <c>calendar-mcp</c>).</param>
/// <param name="Tool">Tool name on the server (e.g. <c>get_calendar_events</c>).</param>
/// <param name="Arguments">JSON arguments to pass to the tool. Stored verbatim and round-tripped.</param>
/// <param name="Expect">What outcome the predicate expects when the claim is wrong.</param>
public sealed record VerifyShape(
    string Server,
    string Tool,
    JsonElement Arguments,
    VerifyExpectation Expect);
