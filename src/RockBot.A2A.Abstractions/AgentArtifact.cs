namespace RockBot.A2A;

/// <summary>
/// An output artifact produced by an agent task.
/// </summary>
public sealed record AgentArtifact
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<AgentMessagePart> Parts { get; init; }
}
