namespace RockBot.Host;

/// <summary>
/// Composes an LLM system prompt from an <see cref="AgentProfile"/> and <see cref="AgentIdentity"/>.
/// </summary>
public interface ISystemPromptBuilder
{
    /// <summary>
    /// Builds the system prompt string.
    /// </summary>
    string Build(AgentProfile profile, AgentIdentity identity);

    /// <summary>
    /// Builds the system prompt string, optionally appending a category-scoped hint
    /// from <c>{agent-profile}/prompt-hints/{category}.md</c>. Implementations that
    /// don't support hints fall back to the parameterless overload.
    /// See <c>design/self-repair.md</c> Phase 4 — <see cref="RepairTarget.PromptBuilderHint"/>.
    /// </summary>
    string Build(AgentProfile profile, AgentIdentity identity, string? category) => Build(profile, identity);
}
