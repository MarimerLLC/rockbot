namespace RockBot.Host;

/// <summary>
/// Estimates LLM call cost in USD from token counts and model ID.
/// Uses a static lookup of well-known models; unknown models return 0.
/// Update this table as pricing changes or new models are added.
/// </summary>
internal static class LlmCostEstimator
{
    /// <summary>Cost per million tokens (input, output) in USD.</summary>
    private static readonly (string Prefix, double InputPerM, double OutputPerM)[] Table =
    [
        // Claude models (Anthropic / OpenRouter)
        ("claude-opus-4",        15.00, 75.00),
        ("claude-sonnet-4",       3.00, 15.00),
        ("claude-haiku-4",        0.80,  4.00),
        ("claude-3-opus",        15.00, 75.00),
        ("claude-3-5-sonnet",     3.00, 15.00),
        ("claude-3-5-haiku",      0.80,  4.00),
        ("claude-3-haiku",        0.25,  1.25),

        // OpenAI models
        ("gpt-5.3",               1.75, 14.00),
        ("gpt-4o-mini",           0.15,  0.60),
        ("gpt-4o",                2.50, 10.00),
        ("gpt-4-turbo",          10.00, 30.00),
        ("o1-mini",               1.10,  4.40),
        ("o1",                   15.00, 60.00),
        ("o3-mini",               1.10,  4.40),

        // Google models
        ("gemini-3.1-pro",        2.00, 12.00),
        ("gemini-3-flash",        0.50,  3.00),
        ("gemini-2.0-flash",      0.10,  0.40),
        ("gemini-2.5-flash",      0.15,  0.60),
        ("gemini-1.5-flash",      0.075, 0.30),
        ("gemini-1.5-pro",        1.25,  5.00),
        ("gemini-2.5-pro",        1.25,  10.00),

        // DeepSeek models
        ("deepseek-chat",         0.14,  0.28),
        ("deepseek-r1",           0.55,  2.19),
    ];

    /// <summary>
    /// Estimates cost in USD for a single LLM call.
    /// Returns 0 if the model is not in the lookup table.
    /// </summary>
    public static double EstimateCost(string modelId, long inputTokens, long outputTokens)
    {
        foreach (var (prefix, inputPerM, outputPerM) in Table)
        {
            if (modelId.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return (inputTokens * inputPerM + outputTokens * outputPerM) / 1_000_000.0;
        }
        return 0.0;
    }
}
