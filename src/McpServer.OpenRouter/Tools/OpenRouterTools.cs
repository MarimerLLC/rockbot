using System.ComponentModel;
using McpServer.OpenRouter.Services;
using ModelContextProtocol.Server;

namespace McpServer.OpenRouter.Tools;

/// <summary>
/// MCP tools that expose read-only OpenRouter account and usage information.
/// No tools that can purchase credits or spend money are included.
/// </summary>
[McpServerToolType]
public sealed class OpenRouterTools(OpenRouterClient client)
{
    [McpServerTool(Name = "get_credits")]
    [Description("Returns the current credit balance for the OpenRouter account.")]
    public async Task<string> GetCreditsAsync(CancellationToken ct)
    {
        var result = await client.GetCreditsAsync(ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Name = "get_api_key_info")]
    [Description("Returns details about the current API key, including rate limits and usage.")]
    public async Task<string> GetApiKeyInfoAsync(CancellationToken ct)
    {
        var result = await client.GetApiKeyInfoAsync(ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Name = "list_models")]
    [Description("Lists all available models on OpenRouter, including context lengths and pricing.")]
    public async Task<string> ListModelsAsync(CancellationToken ct)
    {
        var result = await client.ListModelsAsync(ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Name = "list_api_keys")]
    [Description("Lists all provisioned API keys for the organisation. Requires a management key.")]
    public async Task<string> ListApiKeysAsync(CancellationToken ct)
    {
        var result = await client.ListApiKeysAsync(ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Name = "get_api_key")]
    [Description("Returns details for a specific provisioned API key identified by its hash. Requires a management key.")]
    public async Task<string> GetApiKeyAsync(
        [Description("The hash identifier of the API key to retrieve.")] string keyHash,
        CancellationToken ct)
    {
        var result = await client.GetApiKeyAsync(keyHash, ct);
        return result?.ToJsonString() ?? "{}";
    }

    [McpServerTool(Name = "get_generation")]
    [Description("Returns details for a specific generation (completion) by its ID, including token usage and cost.")]
    public async Task<string> GetGenerationAsync(
        [Description("The generation ID returned by the completions API.")] string generationId,
        CancellationToken ct)
    {
        var result = await client.GetGenerationAsync(generationId, ct);
        return result?.ToJsonString() ?? "{}";
    }
}
