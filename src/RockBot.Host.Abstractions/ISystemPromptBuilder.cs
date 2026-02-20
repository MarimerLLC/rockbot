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
}
