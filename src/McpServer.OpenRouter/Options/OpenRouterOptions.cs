namespace McpServer.OpenRouter.Options;

/// <summary>
/// Configuration options for the OpenRouter MCP server.
/// Bind from configuration section "OpenRouter".
/// The API key should come from user secrets, environment variables, or a Kubernetes secret —
/// never from committed configuration files.
/// </summary>
public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    /// <summary>
    /// Base URL for the OpenRouter API. Defaults to https://openrouter.ai/api/v1.
    /// </summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// OpenRouter management API key (or standard API key).
    /// Required. Provide via user secrets, environment variable
    /// OpenRouter__ApiKey, or a Kubernetes secret.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
