namespace RockBot.A2A;

/// <summary>
/// A skill that an agent can perform, advertised in its <see cref="AgentCard"/>.
/// </summary>
public sealed record AgentSkill
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Examples { get; init; }
}
