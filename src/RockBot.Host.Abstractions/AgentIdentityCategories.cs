namespace RockBot.Host;

/// <summary>
/// Well-known long-term memory category names for the agent's mutable narrative identity.
/// These entries are agent-written (via the dream service) and user-reviewable/editable
/// via standard memory tools. They complement but never override the immutable soul.md.
/// </summary>
public static class AgentIdentityCategories
{
    /// <summary>Category prefix for all identity entries.</summary>
    public const string Prefix = "agent-identity";

    /// <summary>Current understanding of purpose — how the agent interprets its mission given experience.</summary>
    public const string Mission = "agent-identity/mission";

    /// <summary>Long-term goals derived from user patterns and feedback.</summary>
    public const string Goals = "agent-identity/goals";

    /// <summary>Active projects and their status.</summary>
    public const string Projects = "agent-identity/projects";

    /// <summary>Self-assessed strengths and limitations based on accumulated experience.</summary>
    public const string Capabilities = "agent-identity/capabilities";

    /// <summary>How the agent describes itself based on experience — the narrative self-model.</summary>
    public const string SelfModel = "agent-identity/self-model";
}
