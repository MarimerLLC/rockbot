namespace RockBot.Tools;

/// <summary>
/// Pre-flight recovery hook for callers that detect schema-mismatch problems
/// before invoking an MCP tool — most notably wisp Direct MCP steps, whose
/// schema validator catches missing required fields without going through the
/// post-flight <c>McpRecoveryExecutor</c> path. Implementations resolve
/// environmental defaults (time zone, current time, etc.) silently and build
/// an enriched-error context string for fields they can't fill, so callers
/// can pass the same schema/description/session-history hints to a downstream
/// LLM correction step that the post-flight enricher would have provided.
///
/// See <c>design/self-repair.md</c> Amendment 1 and the wisp pre-flight gap
/// addressed alongside it.
/// </summary>
public interface IMcpPreflightRecovery
{
    /// <summary>
    /// Given a set of missing required fields for an MCP tool, returns any
    /// values that environmental-default providers can fill silently plus an
    /// optional enriched-error context (field schemas, tool-description hints,
    /// recent same-session calls) for fields no provider could fill.
    /// </summary>
    /// <param name="serverName">MCP server name.</param>
    /// <param name="toolName">Tool name on the server.</param>
    /// <param name="missingFields">Required fields detected as missing by the caller.</param>
    /// <param name="existingArgs">Arguments already supplied — passed to defaults providers
    /// as resolution context.</param>
    /// <param name="parentSessionId">Session id of the agent that initiated this call,
    /// used by the enricher to surface recent same-session tool calls. May be null.</param>
    Task<PreflightRecoveryResult> TryRecoverAsync(
        string serverName,
        string toolName,
        IReadOnlyList<string> missingFields,
        IReadOnlyDictionary<string, object?> existingArgs,
        string? parentSessionId,
        CancellationToken ct);

    /// <summary>
    /// Returns the JSON parameters schema for an MCP tool as a raw string, or null
    /// when no schema source is available. Lets wisp pre-flight validation succeed
    /// in bridge-mode agents (where per-server tool registrations don't live in the
    /// local tool registry) without RockBot.Wisp taking a direct dependency on the
    /// MCP schema cache. Implementations may transparently fetch from the MCP
    /// bridge on first use and cache for subsequent calls.
    /// </summary>
    Task<string?> TryGetParametersSchemaAsync(
        string serverName, string toolName, CancellationToken ct);
}

/// <summary>
/// Result of <see cref="IMcpPreflightRecovery.TryRecoverAsync"/>.
/// </summary>
/// <param name="FilledDefaults">Field name → value pairs that an environmental-default
/// provider could resolve. The caller merges these into the tool arguments before retrying.
/// Empty when no provider matched.</param>
/// <param name="UnresolvedFields">Subset of the input missing fields for which no provider
/// could supply a value. The caller still has to surface these to a downstream LLM step
/// (or fail the call) — they were not silently fixed.</param>
/// <param name="EnrichedErrorContext">Optional multi-line context string produced by the
/// schema-error enricher for the unresolved fields, suitable for inclusion in an LLM
/// correction prompt. Null when no enrichment was available (e.g. schema lookup failed
/// or every missing field was filled).</param>
public sealed record PreflightRecoveryResult(
    IReadOnlyDictionary<string, object?> FilledDefaults,
    IReadOnlyList<string> UnresolvedFields,
    string? EnrichedErrorContext);
