namespace RockBot.Host;

/// <summary>
/// Configuration for a single LLM tier (endpoint, API key, model ID).
/// </summary>
public sealed class LlmTierConfig
{
    /// <summary>
    /// LLM provider for this tier (e.g. "Copilot"). When null/empty, inherits the
    /// global <c>LLM:Provider</c> value. Allows mixing providers across tiers.
    /// </summary>
    public string? Provider { get; set; }

    public string? Endpoint { get; set; }
    public string? ApiKey   { get; set; }
    public string? ModelId  { get; set; }

    /// <summary>
    /// Returns true when Endpoint, ApiKey, and ModelId are all non-empty
    /// (OpenAI-compatible provider fully configured).
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Endpoint)
                             && !string.IsNullOrEmpty(ApiKey)
                             && !string.IsNullOrEmpty(ModelId);

    /// <summary>
    /// Returns true when this tier has enough configuration to be used independently
    /// (either a full OpenAI-compatible config or just a ModelId for Copilot).
    /// </summary>
    public bool HasModelId => !string.IsNullOrEmpty(ModelId);

    /// <summary>
    /// Returns the effective provider for this tier: the per-tier <see cref="Provider"/>
    /// if set, otherwise the <paramref name="globalProvider"/> fallback.
    /// </summary>
    public string? EffectiveProvider(string? globalProvider) =>
        !string.IsNullOrEmpty(Provider) ? Provider : globalProvider;

    /// <summary>
    /// Returns true when this tier is configured for the Copilot provider.
    /// </summary>
    public bool IsCopilot(string? globalProvider) =>
        string.Equals(EffectiveProvider(globalProvider), "Copilot", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Ordered list of balanced-tier model configs for fallback chain.
    /// When populated, takes precedence over the single <see cref="Balanced"/> entry.
    /// First entry is preferred; subsequent entries are fallbacks in order.</summary>
    public List<LlmTierConfig> BalancedModels { get; set; } = [];

    /// <summary>
    /// Returns the effective config for <paramref name="tier"/>, falling back
    /// to <see cref="Balanced"/> when the requested tier is not configured.
    /// </summary>
    /// <summary>
    /// Returns the effective config for <paramref name="tier"/>, falling back
    /// to <see cref="Balanced"/> when the requested tier is not configured.
    /// A tier is considered configured when it has a full OpenAI-compatible setup
    /// (IsConfigured) or at least a ModelId (sufficient for Copilot provider).
    /// </summary>
    public LlmTierConfig Resolve(ModelTier tier) => tier switch
    {
        ModelTier.Low  => (Low.IsConfigured || Low.HasModelId)   ? Low  : Balanced,
        ModelTier.High => (High.IsConfigured || High.HasModelId) ? High : Balanced,
        _              => Balanced
    };
}
