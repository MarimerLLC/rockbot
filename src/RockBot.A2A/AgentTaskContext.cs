using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// Context provided to <see cref="IAgentTaskHandler"/> implementations.
/// Exposes the underlying message context and a delegate for publishing
/// intermediate status updates.
/// </summary>
public sealed class AgentTaskContext
{
    public required MessageHandlerContext MessageContext { get; init; }
    public required Func<AgentTaskStatusUpdate, CancellationToken, Task> PublishStatus { get; init; }
}
