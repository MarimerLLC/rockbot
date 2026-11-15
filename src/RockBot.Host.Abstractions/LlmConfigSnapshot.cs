using System.Globalization;
using System.Text;

namespace RockBot.Host;

/// <summary>
/// File name for the agent's LLM configuration snapshot. Shared with the introspection sidecar,
/// which reads the file directly rather than through the host.
/// </summary>
public static class LlmConfigFiles
{
    /// <summary>Snapshot file, relative to <see cref="AgentProfileOptions.BasePath"/>.</summary>
    public const string SnapshotFile = "llm-config.json";
}

/// <summary>
/// What models the agent is configured to use: provider, service and model id per tier, with
/// the Balanced fallback chain in priority order. Written once at startup by the agent and read
/// by the introspection sidecar, so the agent can answer "what model are you?" from its real
/// configuration instead of guessing.
/// </summary>
/// <remarks>
/// <para>
/// API keys never enter this type. <see cref="Create"/> copies only the provider, model id and
/// endpoint host; paths and query strings are dropped as well, because some endpoints carry a
/// key there (Azure's <c>?api-key=</c>). The sidecar deliberately has no access to the agent's
/// LLM secret, and this snapshot is how it can still describe the configuration.
/// </para>
/// <para>
/// LLM tier config is environment-only and is read once at startup, so the snapshot is exactly
/// what the running process uses until the next restart.
/// </para>
/// </remarks>
public sealed record LlmConfigSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>When the agent wrote the snapshot, which is when it loaded this configuration.</summary>
    public DateTimeOffset WrittenAt { get; init; }

    public string? AgentVersion { get; init; }

    /// <summary>Set when <c>LLM:FixedTier</c> pins every request to one tier.</summary>
    public string? FixedTier { get; init; }

    /// <summary>Set when there is nothing to list, for example when no LLM is configured.</summary>
    public string? Note { get; init; }

    public IReadOnlyList<LlmTierSnapshot> Tiers { get; init; } = [];

    public LlmEmbeddingSnapshot? Embedding { get; init; }

    /// <summary>
    /// Builds the snapshot from the resolved tier options, mirroring how the agent builds its
    /// chat clients: Balanced uses <see cref="LlmTierOptions.BalancedModels"/> when that list is
    /// populated, and Low and High use the single Balanced entry when they have no model of their own.
    /// </summary>
    /// <param name="llmConfigured">
    /// False when the agent found no LLM configuration and fell back to its echo client.
    /// </param>
    public static LlmConfigSnapshot Create(
        LlmTierOptions tiers,
        string? globalProvider,
        EmbeddingOptions? embedding,
        string? fixedTier,
        string? agentVersion,
        bool llmConfigured,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        var embeddingSnapshot = embedding is { IsConfigured: true }
            ? new LlmEmbeddingSnapshot
            {
                ModelId = embedding.Model!,
                Service = DescribeService(provider: null, embedding.Endpoint),
                EndpointHost = EndpointHost(embedding.Endpoint)
            }
            : null;

        if (!llmConfigured)
        {
            return new LlmConfigSnapshot
            {
                WrittenAt = now,
                AgentVersion = agentVersion,
                Note = "No LLM is configured. The agent is answering with its built-in echo client.",
                Embedding = embeddingSnapshot
            };
        }

        var chainImages = tiers.BalancedChainImageSupport();
        var balanced = tiers.BalancedModels.Count > 0
            ? tiers.BalancedModels.Select((m, i) => Model(m, globalProvider, chainImages[i])).ToList()
            : [Model(tiers.Balanced, globalProvider, tiers.Balanced.SupportsImageInput)];

        return new LlmConfigSnapshot
        {
            WrittenAt = now,
            AgentVersion = agentVersion,
            FixedTier = string.IsNullOrWhiteSpace(fixedTier) ? null : fixedTier,
            Tiers =
            [
                Tier(ModelTier.Low, tiers.Low, tiers, globalProvider),
                new LlmTierSnapshot
                {
                    Tier = nameof(ModelTier.Balanced),
                    Models = balanced,
                    // The same rule the agent uses to decide whether to send images to Balanced.
                    AcceptsImages = tiers.TierSupportsImageInput(ModelTier.Balanced)
                },
                Tier(ModelTier.High, tiers.High, tiers, globalProvider)
            ],
            Embedding = embeddingSnapshot
        };
    }

    /// <summary>
    /// Plain-language summary, one line per tier, e.g.
    /// <c>Balanced: claude-haiku-5-5 (Anthropic API), then anthropic/claude-haiku-5.5 (OpenRouter)</c>.
    /// </summary>
    public string Describe()
    {
        if (Tiers.Count == 0)
            return Note ?? "No LLM tiers are configured.";

        var sb = new StringBuilder();
        foreach (var tier in Tiers)
        {
            sb.Append(tier.Tier).Append(": ");
            sb.Append(string.Join(", then ", tier.Models.Select(m =>
                // In a chain that takes images, name the models image requests skip.
                tier.AcceptsImages && tier.Models.Count > 1 && !m.SupportsImageInput
                    ? $"{m.ModelId} ({m.Service}, text only)"
                    : $"{m.ModelId} ({m.Service})")));
            if (tier.AcceptsImages)
                sb.Append(" [accepts images]");
            if (tier.UsesTier is not null)
                sb.Append(CultureInfo.InvariantCulture, $" [no model of its own; uses the {tier.UsesTier} model]");
            else if (tier.Models.Count > 1)
                sb.Append(" [fallback chain: the first is used, and each later model is used only when the ones before it fail]");
            sb.Append('\n');
        }

        if (FixedTier is not null)
            sb.Append(CultureInfo.InvariantCulture, $"Every request is pinned to the {FixedTier} tier (LLM:FixedTier).\n");

        if (Embedding is not null)
            sb.Append(CultureInfo.InvariantCulture, $"Embeddings: {Embedding.ModelId} ({Embedding.Service})\n");

        return sb.ToString().TrimEnd('\n');
    }

    private static LlmTierSnapshot Tier(
        ModelTier tier, LlmTierConfig own, LlmTierOptions tiers, string? globalProvider)
    {
        // Same test LlmTierOptions.Resolve applies, so the snapshot cannot disagree with the
        // client the agent actually built for this tier.
        var resolved = tiers.Resolve(tier);
        return new LlmTierSnapshot
        {
            Tier = tier.ToString(),
            UsesTier = ReferenceEquals(resolved, own) ? null : nameof(ModelTier.Balanced),
            Models = [Model(resolved, globalProvider, resolved.SupportsImageInput)],
            AcceptsImages = tiers.TierSupportsImageInput(tier)
        };
    }

    private static LlmModelSnapshot Model(LlmTierConfig config, string? globalProvider, bool supportsImageInput)
    {
        var provider = config.IsCopilot(globalProvider) ? "Copilot"
            : config.IsAnthropic(globalProvider) ? "Anthropic"
            // Every other value, including "OpenAI" and empty, takes the OpenAI-compatible path.
            : "OpenAI-compatible";

        return new LlmModelSnapshot
        {
            Provider = provider,
            Service = DescribeService(provider, config.Endpoint),
            EndpointHost = provider == "Copilot" ? null
                : EndpointHost(config.Endpoint) ?? (provider == "Anthropic" ? "api.anthropic.com" : null),
            ModelId = string.IsNullOrEmpty(config.ModelId) ? "(not set)" : config.ModelId,
            SupportsImageInput = supportsImageInput,
            ReasoningEffort = string.IsNullOrWhiteSpace(config.ReasoningEffort) ? null : config.ReasoningEffort
        };
    }

    /// <summary>
    /// Host (and non-default port) only. The path, query and any user-info are dropped, because
    /// some endpoints put a key in the query string.
    /// </summary>
    internal static string? EndpointHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
            return null;

        return uri.IsDefaultPort
            ? uri.Host
            : string.Create(CultureInfo.InvariantCulture, $"{uri.Host}:{uri.Port}");
    }

    /// <summary>
    /// The service behind the endpoint, in the words a person would use. The provider alone can't
    /// say this: OpenRouter's Messages API is reached with the Anthropic provider, just like the
    /// Claude API is, so only the endpoint tells the two apart.
    /// </summary>
    internal static string DescribeService(string? provider, string? endpoint)
    {
        if (provider == "Copilot") return "GitHub Copilot";

        var host = EndpointHost(endpoint)?.Split(':')[0].ToLowerInvariant();
        if (host is null)
            return provider == "Anthropic" ? "Anthropic API" : "unknown";

        if (host is "openrouter.ai" || host.EndsWith(".openrouter.ai", StringComparison.Ordinal))
            return "OpenRouter";
        if (host is "api.anthropic.com")
            return "Anthropic API";
        if (host is "api.openai.com")
            return "OpenAI";
        if (host.EndsWith(".openai.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".services.ai.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".models.ai.azure.com", StringComparison.Ordinal))
            return "Azure AI Foundry";
        if (host is "generativelanguage.googleapis.com")
            return "Google Gemini API";
        if (host is "api.x.ai")
            return "xAI";
        if (host.Contains("ollama", StringComparison.Ordinal) || EndpointHost(endpoint)!.EndsWith(":11434", StringComparison.Ordinal))
            return "Ollama";

        return host;
    }
}

/// <summary>One tier's models, in the order the agent tries them.</summary>
public sealed record LlmTierSnapshot
{
    public string Tier { get; init; } = "";

    /// <summary>
    /// Set when this tier has no model of its own and uses another tier's (always Balanced).
    /// </summary>
    public string? UsesTier { get; init; }

    public IReadOnlyList<LlmModelSnapshot> Models { get; init; } = [];

    /// <summary>
    /// Whether the agent will send images to this tier (<see cref="LlmTierOptions.TierSupportsImageInput"/>).
    /// For a fallback chain this is true when any model can read images; image requests then go
    /// only to the models whose <see cref="LlmModelSnapshot.SupportsImageInput"/> is true.
    /// </summary>
    public bool AcceptsImages { get; init; }
}

/// <summary>One configured model. Carries no credential of any kind.</summary>
public sealed record LlmModelSnapshot
{
    /// <summary><c>Anthropic</c>, <c>Copilot</c> or <c>OpenAI-compatible</c>: the client the agent uses.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Who serves the model, e.g. <c>Anthropic API</c>, <c>OpenRouter</c>, <c>Azure AI Foundry</c>.</summary>
    public string Service { get; init; } = "";

    public string? EndpointHost { get; init; }

    public string ModelId { get; init; } = "";

    /// <summary>
    /// Whether this model is given requests that carry images. In a fallback chain, image
    /// requests skip the models where this is false.
    /// </summary>
    public bool SupportsImageInput { get; init; }

    public string? ReasoningEffort { get; init; }
}

/// <summary>The text-embedding model used for hybrid search.</summary>
public sealed record LlmEmbeddingSnapshot
{
    public string ModelId { get; init; } = "";

    public string Service { get; init; } = "";

    public string? EndpointHost { get; init; }
}
