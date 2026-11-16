namespace RockBot.UserProxy;

// The agent-health sections of AgentIntrospectionResponse (#834, stage 4): MCP servers, skills,
// memory health and tier routing. Like the rest of the response, nothing here is a credential:
// endpoints are a host only, and nothing from an MCP server's headers, environment or auth goes out.

/// <summary>The MCP servers the agent's bridge is configured with.</summary>
public sealed record McpIntrospection
{
    public IReadOnlyList<McpServerIntrospection> Servers { get; init; } = [];
}

public sealed record McpServerIntrospection
{
    /// <summary>The server's name in the agent's <c>mcp.json</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The name the server gives itself, when it differs.</summary>
    public string? DisplayName { get; init; }

    /// <summary><c>http</c> or <c>stdio</c>.</summary>
    public string Transport { get; init; } = "";

    /// <summary>The endpoint's host, for an HTTP server.</summary>
    public string? Host { get; init; }

    /// <summary>
    /// <c>connected</c>, <c>sign-in required</c>, <c>sign-in pending</c>, <c>failed</c> (the last
    /// connect attempt failed; see <see cref="LastError"/>) or <c>not connected</c> (not tried yet,
    /// or retired).
    /// </summary>
    public string State { get; init; } = "";

    /// <summary>The server signs the agent in with OAuth.</summary>
    public bool UsesOAuth { get; init; }

    /// <summary>Tools the agent can call, after its allow and deny lists. Zero when not connected.</summary>
    public int ToolCount { get; init; }

    public int PromptCount { get; init; }

    public int ResourceCount { get; init; }

    /// <summary>The version the server reports.</summary>
    public string? Version { get; init; }

    /// <summary>Why the last connect attempt failed, with URLs cut to scheme, host and path. Only while not connected.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastErrorAt { get; init; }
}

/// <summary>The skills the agent has.</summary>
public sealed record SkillsIntrospection
{
    public int Count { get; init; }

    /// <summary>Skills that ship with the agent.</summary>
    public int Starter { get; init; }

    /// <summary>Skills the agent wrote from experience.</summary>
    public int Learned { get; init; }

    /// <summary>Skills generated for an MCP server's tools (<c>mcp/…</c>).</summary>
    public int FromMcp { get; init; }

    /// <summary>Every skill, by name.</summary>
    public IReadOnlyList<SkillIntrospection> Skills { get; init; } = [];
}

public sealed record SkillIntrospection
{
    public string Name { get; init; } = "";

    /// <summary>One or two sentences; empty while the agent is still writing it.</summary>
    public string Summary { get; init; } = "";

    /// <summary><c>starter</c>, <c>learned</c> or <c>mcp</c>.</summary>
    public string Source { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
/// Memory health, from the latest memory audit (the agent audits its long-term memory on a
/// schedule; this reads the result and never starts an audit).
/// </summary>
public sealed record MemoryIntrospection
{
    /// <summary>When the latest audit ran. Null when none has yet.</summary>
    public DateTimeOffset? AuditedAt { get; init; }

    /// <summary><c>healthy</c>, <c>warning</c> or <c>alert</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Live long-term memories at the audit.</summary>
    public int Live { get; init; }

    public int Archived { get; init; }

    public int CreatedSinceLast { get; init; }

    public int ArchivedSinceLast { get; init; }

    public int HardDeletedSinceLast { get; init; }

    /// <summary>Net live entries gained per day since the audit before.</summary>
    public double? NetGrowthPerDay { get; init; }

    /// <summary>Pairs of live entries that read as near-duplicates.</summary>
    public int NearDuplicatePairs { get; init; }

    /// <summary>Entries the retention purge will remove within <see cref="PurgeDueWithinDays"/> days.</summary>
    public int PurgeDueCount { get; init; }

    public int PurgeDueWithinDays { get; init; }

    /// <summary>What the audit found wrong, if anything.</summary>
    public IReadOnlyList<MemoryFindingIntrospection> Findings { get; init; } = [];

    /// <summary>The share of sampled memories an LLM judge found sound, at the last eval (0–1).</summary>
    public double? EvalSoundRate { get; init; }

    public DateTimeOffset? EvaluatedAt { get; init; }

    public DateTimeOffset? ConsolidationLastRunAt { get; init; }

    /// <summary>Dream consolidation is stopped until someone resumes it (the audit pauses it on data loss).</summary>
    public bool ConsolidationPaused { get; init; }

    public string? ConsolidationPausedReason { get; init; }

    public DateTimeOffset? ConsolidationPausedAt { get; init; }
}

public sealed record MemoryFindingIntrospection
{
    public string Name { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>How the agent's requests were routed across the model tiers over a window.</summary>
public sealed record RoutingIntrospection
{
    public int WindowDays { get; init; }

    /// <summary>The first and last routed request in the window.</summary>
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Through { get; init; }

    public int Total { get; init; }

    public int UserMessages { get; init; }

    public int Subagents { get; init; }

    /// <summary>Requests a tier's primary model failed, so a fallback answered.</summary>
    public int Fallbacks { get; init; }

    public double FallbackPct { get; init; }

    public IReadOnlyList<TierRoutingIntrospection> Tiers { get; init; } = [];
}

public sealed record TierRoutingIntrospection
{
    public string Tier { get; init; } = "";

    public int Count { get; init; }

    public double Pct { get; init; }

    public long? AvgLatencyMs { get; init; }

    public long? AvgInputTokens { get; init; }

    public long? AvgOutputTokens { get; init; }

    public double AvgToolCalls { get; init; }
}
