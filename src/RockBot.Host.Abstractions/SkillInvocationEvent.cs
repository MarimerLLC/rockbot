namespace RockBot.Host;

/// <summary>
/// Records a single invocation of an agent skill — when it was retrieved and in which session.
/// </summary>
public sealed record SkillInvocationEvent(
    string Id,
    string SkillName,
    string SessionId,
    DateTimeOffset Timestamp);
