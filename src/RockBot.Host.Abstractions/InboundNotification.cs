namespace RockBot.Host;

/// <summary>
/// Represents a notification from an inbound A2A task that should be
/// presented to the user when they are idle.
/// </summary>
public sealed record InboundNotification
{
    /// <summary>A2A task ID.</summary>
    public required string TaskId { get; init; }

    /// <summary>Display name of the calling agent.</summary>
    public required string CallerName { get; init; }

    /// <summary>LLM-generated or handler-generated summary of the request.</summary>
    public required string Summary { get; init; }

    /// <summary>When the notification was created.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The A2A skill that was invoked, if any.</summary>
    public string? SkillId { get; init; }
}
