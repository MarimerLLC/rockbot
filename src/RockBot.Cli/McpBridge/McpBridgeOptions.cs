namespace RockBot.Cli.McpBridge;

/// <summary>
/// Options for the MCP Bridge service.
/// </summary>
public sealed class McpBridgeOptions
{
    /// <summary>
    /// Path to the mcp.json configuration file.
    /// </summary>
    public string ConfigPath { get; set; } = "mcp.json";

    /// <summary>
    /// Default timeout in milliseconds for MCP server calls.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// When true, the bridge calls the LLM to generate a one-sentence summary of each
    /// connected server's capabilities before publishing <see cref="McpServersIndexed"/>.
    /// Falls back to a simple tool-list summary if the LLM is unavailable or the call fails.
    /// </summary>
    public bool GenerateLlmSummaries { get; set; } = true;
}
