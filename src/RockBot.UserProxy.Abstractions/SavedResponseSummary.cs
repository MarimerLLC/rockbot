namespace RockBot.UserProxy;

/// <summary>
/// Lightweight summary of a saved response, used in list results.
/// </summary>
/// <param name="Source">
/// What saved it: "user", "agent", or the kind of work that was saved automatically
/// ("subagent", "scheduled", "a2a", "background-task", "reply"). Null from agents older than #418.
/// </param>
public sealed record SavedResponseSummary(
    string Id,
    string Label,
    string AgentName,
    DateTimeOffset SavedAt,
    string? Source = null);
