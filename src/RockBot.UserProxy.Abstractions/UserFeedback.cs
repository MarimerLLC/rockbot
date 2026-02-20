namespace RockBot.UserProxy;

/// <summary>
/// Feedback from a human user on a specific agent reply.
/// Published on <see cref="UserProxyTopics.UserFeedback"/> fire-and-forget.
/// </summary>
/// <remarks>
/// On positive feedback the agent should reinforce the response pattern in its conversation
/// history (e.g. append a note that the user found the reply helpful) so future turns benefit
/// from the signal.
///
/// On negative feedback the agent should re-evaluate its last response and send a fresh
/// <see cref="AgentReply"/> back on <see cref="UserProxyTopics.UserResponse"/>.  The proxy
/// forwards unsolicited replies directly to the frontend so they appear in the chat without
/// the user having to send a new message.
/// </remarks>
public sealed record UserFeedback
{
    public required string SessionId { get; init; }

    /// <summary>ID of the <see cref="ChatMessage"/> the user is reacting to.</summary>
    public required string MessageId { get; init; }

    /// <summary>True = thumbs up (positive); false = thumbs down (negative).</summary>
    public required bool IsPositive { get; init; }

    /// <summary>Name of the agent that produced the message, if known.</summary>
    public string? AgentName { get; init; }
}
