using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpServer.Introspection.Tools;

[McpServerToolType]
public sealed class AgentNameTools(IConfiguration configuration)
{
    private string NameFilePath => configuration["AgentName:Path"] ?? "/data/agent/agent-name.md";

    [McpServerTool(Name = "get_agent_name")]
    [Description(
        "Returns the agent's current display name. If no custom name has been set, " +
        "returns an empty result indicating the agent is using its default identity name.")]
    public async Task<string> GetAgentNameAsync()
    {
        if (!File.Exists(NameFilePath))
            return "No custom agent name is set. The agent is using its default identity name.";

        var content = await File.ReadAllTextAsync(NameFilePath);
        var name = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        return string.IsNullOrWhiteSpace(name)
            ? "No custom agent name is set. The agent is using its default identity name."
            : $"The agent's current display name is: {name}";
    }

    [McpServerTool(Name = "set_agent_name")]
    [Description(
        "Sets the agent's display name. This changes how the agent identifies itself " +
        "in conversations and in the UI. The change takes effect immediately via hot-reload. " +
        "Pass an empty string to clear the custom name and revert to the default identity name.")]
    public async Task<string> SetAgentNameAsync(
        [Description("The new display name for the agent. Pass empty string to clear.")] string name)
    {
        var directory = Path.GetDirectoryName(NameFilePath);
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // Clear the name by writing an empty file
            await File.WriteAllTextAsync(NameFilePath, string.Empty);
            return "Agent display name cleared. The agent will use its default identity name.";
        }

        await File.WriteAllTextAsync(NameFilePath, trimmed + Environment.NewLine);
        return $"Agent display name set to: {trimmed}. The change will take effect via hot-reload.";
    }
}
