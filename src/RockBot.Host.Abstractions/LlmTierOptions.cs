namespace RockBot.Host;

/// <summary>
/// Configuration for a single LLM tier (endpoint, API key, model ID).
/// </summary>
public sealed class LlmTierConfig
{
    public string? Endpoint { get; set; }
    public string? ApiKey   { get; set; }
    public string? ModelId  { get; set; }

    /// <summary>
    /// Returns true when all three fields are non-empty.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Endpoint)
                             && !string.IsNullOrEmpty(ApiKey)
                             && !string.IsNullOrEmpty(ModelId);
}

/// <summary>
/// Three-tier LLM configuration (Low / Balanced / High).
/// Low and High fall back to Balanced when not configured.
/// Bind this from the "LLM" config section using the sub-keys
/// <c>Balanced</c>, <c>Low</c>, and <c>High</c> (e.g.
/// <c>LLM__Balanced__Endpoint</c> as an environment variable).
/// </summary>
public sealed class LlmTierOptions
{
    public LlmTierConfig Low      { get; set; } = new();
    public LlmTierConfig Balanced { get; set; } = new();
    public LlmTierConfig High     { get; set; } = new();

    /// <summary>
    /// Returns the effective config for <paramref name="tier"/>, falling back
    /// to <see cref="Balanced"/> when the requested tier is not configured.
    /// </summary>
    public LlmTierConfig Resolve(ModelTier tier) => tier switch
    {
        ModelTier.Low  => Low.IsConfigured  ? Low  : Balanced,
        ModelTier.High => High.IsConfigured ? High : Balanced,
        _              => Balanced
    };
}
