namespace RockBot.Tools;

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
}
