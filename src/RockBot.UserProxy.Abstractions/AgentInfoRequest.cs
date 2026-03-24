namespace RockBot.UserProxy;

/// <summary>
/// Requests agent identity metadata (name, version).
/// Lightweight request — no LLM invocation, deterministic response.
/// </summary>
public sealed record AgentInfoRequest;
