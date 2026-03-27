namespace RockBot.Host;

/// <summary>
/// A user-saved agent response, persisted for later reference.
/// </summary>
public sealed record SavedResponse(
    string Id,
    string Label,
    string Content,
    string AgentName,
    string SessionId,
    DateTimeOffset SavedAt);
