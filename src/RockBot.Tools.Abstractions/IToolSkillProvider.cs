namespace RockBot.Tools;

/// <summary>
/// How the dream service should treat skills under a given name prefix during consolidation.
/// </summary>
public enum ConsolidationPolicy
{
    /// <summary>
    /// Suffixes under the prefix are topical variants and may be merged together or under
    /// an abstract parent guide. This is the default treatment for any prefix cluster.
    /// </summary>
    TopicalCluster,

    /// <summary>
    /// Each suffix is a 1:1 binding to a live external entity (e.g. an MCP server name).
    /// Skills under such a prefix must not be merged across or replaced by a parent guide —
    /// doing so destroys the binding the agent will rebuild on its next encounter.
    /// </summary>
    NamespacedSingleton
}

/// <summary>
/// Implemented by tool services (web, MCP, scripts, etc.) to publish a usage guide
/// that the agent can retrieve on-demand via the <c>get_tool_guide</c> tool.
///
/// Register implementations via DI (multiple registrations are supported):
/// <code>
///   builder.Services.AddSingleton&lt;IToolSkillProvider, MyToolSkillProvider&gt;();
/// </code>
/// The guide becomes available to the agent automatically when that service is in scope.
/// </summary>
public interface IToolSkillProvider
{
    /// <summary>
    /// Short identifier the agent uses to request this guide (e.g. <c>"web"</c>, <c>"mcp"</c>).
    /// Must be unique across all registered providers. Lowercase, no spaces.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// One-line description shown in the guide index so the agent can decide
    /// whether to fetch the full document (e.g. "Web search and page browsing tools").
    /// </summary>
    string Summary { get; }

    /// <summary>
    /// Returns the full markdown usage document for this tool service.
    /// Called only when the agent explicitly requests the guide by name.
    /// </summary>
    string GetDocument();

    /// <summary>
    /// Optional declaration that skills under a given name prefix should be treated by
    /// the dream consolidation pass with a non-default <see cref="ConsolidationPolicy"/>.
    /// Return <c>null</c> (the default) to accept the standard topical-cluster treatment.
    /// </summary>
    /// <example>
    /// MCP server skills are namespaced singletons:
    /// <code>
    /// public (string Prefix, ConsolidationPolicy Policy)? ConsolidationPolicy
    ///     =&gt; ("mcp/", ConsolidationPolicy.NamespacedSingleton);
    /// </code>
    /// </example>
    (string Prefix, ConsolidationPolicy Policy)? ConsolidationPolicy => null;
}
