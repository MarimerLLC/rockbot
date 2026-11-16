namespace RockBot.UserProxy;

/// <summary>
/// The sections an <see cref="AgentIntrospectionRequest"/> can ask for (#834). Names are
/// case-insensitive; an agent ignores names it doesn't know and leaves that section out.
/// </summary>
public static class IntrospectionSections
{
    /// <summary>The configured models: each tier with its fallback chain, embeddings, the decision model.</summary>
    public const string Models = "models";

    /// <summary>The pricing table the agent estimates cost with.</summary>
    public const string Pricing = "pricing";

    /// <summary>Tokens and estimated cost: today, the last 7 and 30 days, per model, and the asking thread.</summary>
    public const string Usage = "usage";

    /// <summary>Every section this version knows, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Models, Pricing, Usage];
}

/// <summary>
/// Asks the agent to describe itself: what it is configured with, what it charges against. The
/// agent answers from its own state without invoking the LLM, so this works even while the LLM is
/// failing (#834). The answer is an <see cref="AgentIntrospectionResponse"/> on the request's
/// reply topic. An agent older than this contract doesn't answer at all; a client treats the
/// timeout as "this agent doesn't report that".
/// </summary>
public sealed record AgentIntrospectionRequest
{
    /// <summary>
    /// The <see cref="IntrospectionSections"/> wanted. Null or empty asks for every section the
    /// agent knows.
    /// </summary>
    public IReadOnlyList<string>? Sections { get; init; }

    /// <summary>
    /// The conversation thread (raw session id) the asker is in. The <see cref="IntrospectionSections.Usage"/>
    /// section then includes that thread's own totals. Optional.
    /// </summary>
    public string? ThreadId { get; init; }
}

/// <summary>
/// The agent's answer to an <see cref="AgentIntrospectionRequest"/>. Each section is null when it
/// wasn't asked for, or when the agent can't report it. Carries no credential of any kind: no API
/// keys, and endpoints only as a host.
/// </summary>
public sealed record AgentIntrospectionResponse
{
    public required string AgentName { get; init; }

    public required string AgentVersion { get; init; }

    /// <summary>When the agent process started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public ModelsIntrospection? Models { get; init; }

    public PricingIntrospection? Pricing { get; init; }

    public UsageIntrospection? Usage { get; init; }
}

/// <summary>
/// The LLM tokens the agent has used and what they cost, by its own estimate from its pricing
/// table. Every request the agent sends to a provider is counted: conversations, subagents,
/// workers, scheduled tasks and the agent's own background work.
/// </summary>
public sealed record UsageIntrospection
{
    /// <summary>The IANA (or Windows) time zone days are counted in: the agent's home zone.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>When the agent started keeping these totals. Older usage isn't counted.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Today's date (<c>yyyy-MM-dd</c>) in <see cref="TimeZone"/>.</summary>
    public string Today { get; init; } = "";

    public UsageTotalsIntrospection TodayTotals { get; init; } = new();

    /// <summary>Today and the six days before it.</summary>
    public UsageTotalsIntrospection Last7Days { get; init; } = new();

    /// <summary>Today and the 29 days before it.</summary>
    public UsageTotalsIntrospection Last30Days { get; init; } = new();

    /// <summary>Each day in the last 30 that had any usage, newest first.</summary>
    public IReadOnlyList<DayUsageIntrospection> Days { get; init; } = [];

    /// <summary>The last 30 days by the model that answered, most expensive first.</summary>
    public IReadOnlyList<ModelUsageIntrospection> Models { get; init; } = [];

    /// <summary>
    /// The last 30 days by workload, most expensive first: <c>session</c> (conversations and
    /// scheduled tasks), <c>subagent</c>, <c>worker</c>, <c>patrol</c> (the agent's own background
    /// work) or <c>unknown</c>.
    /// </summary>
    public IReadOnlyList<KindUsageIntrospection> Kinds { get; init; } = [];

    /// <summary>
    /// The asking thread's totals since the agent started counting: its own turns plus the
    /// subagents and workers it started. Null when the request named no thread, or the thread
    /// has used nothing.
    /// </summary>
    public ThreadUsageIntrospection? Thread { get; init; }
}

/// <summary>Token and cost totals. <see cref="CostUsd"/> is an estimate from the agent's pricing table.</summary>
public sealed record UsageTotalsIntrospection
{
    public long Requests { get; init; }

    /// <summary>All input tokens, including the cached ones and cache writes.</summary>
    public long InputTokens { get; init; }

    public long CachedInputTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public long OutputTokens { get; init; }

    public double CostUsd { get; init; }

    /// <summary>Requests whose model matched no pricing row, so they are costed at $0 here.</summary>
    public long UnpricedRequests { get; init; }
}

public sealed record DayUsageIntrospection
{
    /// <summary><c>yyyy-MM-dd</c>, in <see cref="UsageIntrospection.TimeZone"/>.</summary>
    public string Date { get; init; } = "";

    public UsageTotalsIntrospection Totals { get; init; } = new();
}

public sealed record ModelUsageIntrospection
{
    public string ModelId { get; init; } = "";

    public UsageTotalsIntrospection Totals { get; init; } = new();
}

public sealed record KindUsageIntrospection
{
    public string Kind { get; init; } = "";

    public UsageTotalsIntrospection Totals { get; init; } = new();
}

public sealed record ThreadUsageIntrospection
{
    public string ThreadId { get; init; } = "";

    public DateTimeOffset FirstAt { get; init; }

    public DateTimeOffset LastAt { get; init; }

    public UsageTotalsIntrospection Totals { get; init; } = new();
}

/// <summary>
/// What models the agent is configured to use. LLM configuration is read once at startup, so this
/// is what the running process uses until its next restart.
/// </summary>
public sealed record ModelsIntrospection
{
    /// <summary>When the agent loaded this configuration (its startup).</summary>
    public DateTimeOffset LoadedAt { get; init; }

    /// <summary>A plain-language summary, one line per tier.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Set when <c>LLM:FixedTier</c> pins every request to one tier.</summary>
    public string? FixedTier { get; init; }

    /// <summary>Set when there is nothing to list, for example when no LLM is configured.</summary>
    public string? Note { get; init; }

    /// <summary>Low, Balanced and High, in that order.</summary>
    public IReadOnlyList<TierIntrospection> Tiers { get; init; } = [];

    public EmbeddingIntrospection? Embedding { get; init; }

    /// <summary>The shared decision model, or null when none is configured.</summary>
    public DecisionModelIntrospection? DecisionModel { get; init; }
}

/// <summary>One tier's models, in the order the agent tries them.</summary>
public sealed record TierIntrospection
{
    /// <summary><c>Low</c>, <c>Balanced</c> or <c>High</c>.</summary>
    public string Tier { get; init; } = "";

    /// <summary>Set when this tier has no model of its own and uses another tier's (always Balanced).</summary>
    public string? UsesTier { get; init; }

    /// <summary>Whether the agent sends images to this tier.</summary>
    public bool AcceptsImages { get; init; }

    /// <summary>
    /// The models in priority order. More than one is a fallback chain: the first is used, and
    /// each later model only when the ones before it fail.
    /// </summary>
    public IReadOnlyList<ModelIntrospection> Models { get; init; } = [];
}

/// <summary>One configured model.</summary>
public sealed record ModelIntrospection
{
    /// <summary><c>Anthropic</c>, <c>Copilot</c> or <c>OpenAI-compatible</c>: the client the agent uses.</summary>
    public string Provider { get; init; } = "";

    /// <summary>Who serves the model, e.g. <c>Anthropic API</c>, <c>OpenRouter</c>, <c>Azure AI Foundry</c>.</summary>
    public string Service { get; init; } = "";

    public string? EndpointHost { get; init; }

    public string ModelId { get; init; } = "";

    /// <summary>Whether this model is given requests that carry images.</summary>
    public bool SupportsImageInput { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>The pricing row the agent costs this model with, or null when none matches (cost is then 0).</summary>
    public PriceIntrospection? Price { get; init; }
}

/// <summary>The text-embedding model used for hybrid memory search.</summary>
public sealed record EmbeddingIntrospection
{
    public string ModelId { get; init; } = "";

    public string Service { get; init; } = "";

    public string? EndpointHost { get; init; }
}

/// <summary>
/// The shared decision model: the small classifier behind the action gate, tier routing, memory
/// dedupe and similar checks.
/// </summary>
public sealed record DecisionModelIntrospection
{
    public string Provider { get; init; } = "";

    public string Service { get; init; } = "";

    public string? EndpointHost { get; init; }

    /// <summary>The deployment name the agent calls.</summary>
    public string Deployment { get; init; } = "";

    /// <summary>Uses switched off in configuration.</summary>
    public IReadOnlyList<string> DisabledUses { get; init; } = [];

    /// <summary>A one-line summary.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>The pricing table the agent estimates LLM cost with.</summary>
public sealed record PricingIntrospection
{
    /// <summary>
    /// The pricing file on the agent's volume the table came from, or null when the agent is using
    /// its built-in fallback table.
    /// </summary>
    public string? LoadedFrom { get; init; }

    /// <summary>
    /// The rows, longest prefix first: a model is priced by the first row whose prefix its id
    /// contains.
    /// </summary>
    public IReadOnlyList<PriceIntrospection> Rows { get; init; } = [];
}

/// <summary>
/// One pricing row, in USD per million tokens. Cost = (input × <see cref="InputPerM"/> + output ×
/// <see cref="OutputPerM"/>) / 1,000,000, with cache reads and writes at their own rates.
/// </summary>
public sealed record PriceIntrospection
{
    public string Prefix { get; init; } = "";

    public double InputPerM { get; init; }

    public double OutputPerM { get; init; }

    /// <summary>Cache reads. Null prices them at <see cref="InputPerM"/>.</summary>
    public double? CachedInputPerM { get; init; }

    /// <summary>Cache writes with the 5-minute TTL. Null means 1.25 × <see cref="InputPerM"/>.</summary>
    public double? CacheWrite5mPerM { get; init; }

    /// <summary>Cache writes with the 1-hour TTL. Null means 2 × <see cref="InputPerM"/>.</summary>
    public double? CacheWrite1hPerM { get; init; }
}
