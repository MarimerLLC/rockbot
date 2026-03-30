namespace RockBot.Host;

/// <summary>
/// Core entity types recognized by the knowledge graph.
/// </summary>
public enum KnowledgeEntityType
{
    /// <summary>Contacts, collaborators, stakeholders.</summary>
    Person,

    /// <summary>Ongoing work, codebases, initiatives.</summary>
    Project,

    /// <summary>Areas of interest, expertise, discussion themes.</summary>
    Topic,

    /// <summary>MCP services, integrations, platforms.</summary>
    Tool,

    /// <summary>Meetings, deadlines, milestones.</summary>
    Event,

    /// <summary>Files, emails, artifacts.</summary>
    Document
}
