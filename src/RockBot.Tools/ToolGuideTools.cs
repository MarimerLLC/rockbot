using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools;

/// <summary>
/// LLM-callable tools that let the agent discover and read usage guides provided by
/// registered tool services (web, MCP, scripts, etc.).
///
/// Exposes two tools:
/// <list type="bullet">
///   <item><c>list_tool_guides</c> — returns the index of available guides</item>
///   <item><c>get_tool_guide</c> — returns the full document for a named guide</item>
/// </list>
///
/// Each tool service registers an <see cref="IToolSkillProvider"/> via DI; this class
/// collects them all at construction time so the guide list reflects exactly what is
/// in scope for this agent process.
/// </summary>
public sealed class ToolGuideTools
{
    private readonly IReadOnlyDictionary<string, IToolSkillProvider> _providers;
    private readonly ILogger<ToolGuideTools> _logger;

    public ToolGuideTools(IEnumerable<IToolSkillProvider> providers, ILogger<ToolGuideTools> logger)
    {
        _providers = providers.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(ListToolGuides),
            AIFunctionFactory.Create(GetToolGuide)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description(
        "List all tool service guides available in this agent. " +
        "Call this when you need to understand how to use a built-in capability " +
        "(such as web search, MCP servers, or script execution) and you haven't loaded its guide yet.")]
    public Task<string> ListToolGuides()
    {
        _logger.LogInformation("Tool call: ListToolGuides()");

        if (_providers.Count == 0)
            return Task.FromResult("No tool guides are registered in this agent.");

        var sb = new StringBuilder();
        sb.AppendLine($"Available tool guides ({_providers.Count}):");
        foreach (var p in _providers.Values.OrderBy(p => p.Name))
            sb.AppendLine($"- {p.Name}: {p.Summary}");

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [Description(
        "Load the full usage guide for a named tool service. " +
        "Call list_tool_guides first to see what names are available, " +
        "then call this with the exact name to get instructions, parameter details, and examples.")]
    public Task<string> GetToolGuide(
        [Description("The guide name as shown by list_tool_guides (e.g. 'web', 'mcp')")] string name)
    {
        _logger.LogInformation("Tool call: GetToolGuide(name={Name})", name);

        if (!_providers.TryGetValue(name, out var provider))
        {
            var available = string.Join(", ", _providers.Keys.OrderBy(k => k));
            return Task.FromResult(
                $"No tool guide named '{name}'. " +
                (available.Length > 0
                    ? $"Available guides: {available}."
                    : "No guides are registered in this agent."));
        }

        return Task.FromResult(provider.GetDocument());
    }
}
