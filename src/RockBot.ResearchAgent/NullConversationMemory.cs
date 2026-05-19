using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>
/// No-op <see cref="IConversationMemory"/> for the ephemeral research agent.
/// Each pod handles one task (session = task id) then exits; persisted turns
/// would have no consumer. <see cref="AgentLoopRunner"/> only touches conversation
/// memory in the Azure content-filter recovery path, which is non-fatal if these
/// calls are no-ops.
/// </summary>
internal sealed class NullConversationMemory : IConversationMemory
{
    public Task AddTurnAsync(string sessionId, ConversationTurn turn,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationTurn>>([]);

    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
