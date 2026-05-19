using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>
/// No-op <see cref="ISkillStore"/> for the ephemeral research agent.
/// The research agent neither persists nor consults skills; this exists solely
/// to satisfy <see cref="AgentLoopRunner"/>'s constructor.
/// </summary>
internal sealed class NullSkillStore : ISkillStore
{
    public Task SaveAsync(Skill skill) => Task.CompletedTask;

    public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);

    public Task<IReadOnlyList<Skill>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Skill>>([]);

    public Task DeleteAsync(string name) => Task.CompletedTask;

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults,
        CancellationToken cancellationToken = default,
        float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}
