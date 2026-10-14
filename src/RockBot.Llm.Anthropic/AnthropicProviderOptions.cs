namespace RockBot.Llm.Anthropic;

/// <summary>
/// Settings for the native Anthropic provider, bound from the <c>LLM:Anthropic</c> section
/// (e.g. <c>LLM__Anthropic__CacheTtl=1h</c>). They apply to every tier that uses the
/// provider; endpoint, API key, model and reasoning effort stay per tier.
/// </summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>
    /// Whether requests carry a top-level <c>cache_control</c> marker. On by default: Claude
    /// never caches without one, and RockBot resends the same system prompt and tool list on
    /// every tool-loop iteration, so leaving it off pays full input price for that prefix
    /// every time. Off only to measure the difference.
    /// </summary>
    public bool PromptCaching { get; set; } = true;

    /// <summary>
    /// Cache lifetime: <c>5m</c> (default, writes at 1.25x input price) or <c>1h</c> (writes
    /// at 2x). Reads cost the same either way. 1h pays off only when the gap between turns
    /// routinely exceeds five minutes but stays under an hour.
    /// </summary>
    public string? CacheTtl { get; set; }

    /// <summary>
    /// Output cap sent when a request sets no <c>MaxOutputTokens</c>. The Messages API
    /// requires <c>max_tokens</c>, and the SDK's own fallback is 1024 — too small for a tool
    /// call carrying a file, and thinking counts against it too.
    /// </summary>
    public int DefaultMaxOutputTokens { get; set; } = 16000;
}
