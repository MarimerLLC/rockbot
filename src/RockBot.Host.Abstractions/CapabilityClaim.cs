namespace RockBot.Host;

/// <summary>
/// A falsifiable assertion about a tool's capability boundary, persisted in long-term memory
/// under category <c>claim/capability/{server}/{tool}</c>. Every claim carries a structured
/// <see cref="VerifyShape"/> that lets a future session falsify it by re-running the call.
/// </summary>
/// <remarks>
/// Capability claims are written exclusively by internal code paths
/// (<see cref="ICapabilityClaimWriter"/>) — the LLM cannot create them directly via tool calls.
/// </remarks>
/// <param name="Server">MCP server the claim is about (e.g. <c>calendar-mcp</c>).</param>
/// <param name="Tool">Tool the claim is about (e.g. <c>get_calendar_events</c>).</param>
/// <param name="Statement">Human-readable statement of the asserted limitation.</param>
/// <param name="Verify">Predicate that falsifies the claim when it succeeds.</param>
/// <param name="Evidence">Free-text evidence entries (error messages, session ids, observation digests) supporting the claim at write time.</param>
/// <param name="CreatedAt">When the claim was first written.</param>
public sealed record CapabilityClaim(
    string Server,
    string Tool,
    string Statement,
    VerifyShape Verify,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt);
