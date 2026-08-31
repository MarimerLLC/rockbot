namespace RockBot.Tools.Mcp;

/// <summary>
/// Configuration for a single MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Logical name for this MCP server.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Command to launch the MCP server process.
    /// </summary>
    public required string Command { get; set; }

    /// <summary>
    /// Arguments to pass to the command.
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// Environment variables to set for the server process.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}
