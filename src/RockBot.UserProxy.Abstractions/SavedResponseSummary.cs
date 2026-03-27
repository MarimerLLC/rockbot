namespace RockBot.UserProxy;

/// <summary>
/// Lightweight summary of a saved response, used in list results.
/// </summary>
public sealed record SavedResponseSummary(
    string Id,
    string Label,
    string AgentName,
    DateTimeOffset SavedAt);
