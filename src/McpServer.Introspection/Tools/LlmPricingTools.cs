using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpServer.Introspection.Tools;

[McpServerToolType]
public sealed class LlmPricingTools(IConfiguration configuration)
{
    private string PricingFilePath =>
        configuration["LlmPricing:Path"] ?? "/data/agent/llm-pricing.json";

    [McpServerTool(Name = "get_llm_pricing")]
    [Description(
        "Returns the agent's LLM pricing table as JSON: an array of " +
        "{prefix, inputPerM, outputPerM} entries giving USD cost per million tokens " +
        "for each model prefix. Cost = (inputTokens * inputPerM + outputTokens * outputPerM) / 1_000_000. " +
        "The first entry whose prefix is contained in the model ID wins (longest-prefix-first ordering). " +
        "Use this to estimate the cost of an LLM call before making it, or to explain pricing to the user.")]
    public async Task<string> GetLlmPricingAsync()
    {
        if (!File.Exists(PricingFilePath))
            return "Pricing file not found at " + PricingFilePath +
                   ". The agent is using its built-in fallback table.";

        return await File.ReadAllTextAsync(PricingFilePath);
    }
}
