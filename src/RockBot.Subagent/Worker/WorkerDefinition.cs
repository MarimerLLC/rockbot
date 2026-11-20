using System.Text.Json.Serialization;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Input to a single worker spawn. Workers are leaf gather tasks — see
/// <c>design/worker-subagents.md</c> for the full contract.
/// </summary>
public sealed record WorkerDefinition
{
    /// <summary>
    /// One-sentence imperative describing what the worker should do. Required.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Optional pre-resolved handoff from the spawning agent (active accounts,
    /// known IDs, etc.). The worker treats this as ground truth and does not
    /// re-investigate facts already supplied here.
    /// </summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary>
    /// Optional override for the working-memory key the worker writes its
    /// structured output to. When null, defaults to <c>worker/&lt;task-id&gt;/result</c>.
    /// Use <see cref="ResolveResultKey"/> for the full path every party reads and writes.
    /// </summary>
    [JsonPropertyName("result_key")]
    public string? ResultKey { get; init; }

    /// <summary>
    /// Resolves <see cref="ResultKey"/> to the absolute working-memory path shared by the
    /// worker (which saves there), <c>spawn_workers</c> (which inlines it), and the spawning
    /// agent (which may fetch it). A bare key such as <c>email-sweep</c> would otherwise mean
    /// three different paths — <c>worker/&lt;id&gt;/email-sweep</c> to the worker's namespaced
    /// save, the raw key to the executor's read, and <c>subagent/&lt;id&gt;/email-sweep</c> to
    /// the spawner's get — so the findings were saved but never found. Keys containing '/'
    /// are already absolute (same rule as <c>WorkingMemoryTools</c>) and pass through.
    /// </summary>
    public string ResolveResultKey(string taskId)
    {
        var key = ResultKey?.Trim();
        if (string.IsNullOrEmpty(key))
            return $"worker/{taskId}/result";
        return key.Contains('/') ? key : $"worker/{taskId}/{key}";
    }

    /// <summary>
    /// Optional working-memory keys holding data the worker should work from instead of fetching
    /// it (#847): a wisp step's output as <c>wisp:&lt;wisp-id&gt;/&lt;step-id&gt;</c>, or any full key
    /// (with a '/'), e.g. <c>wisp/wisp-1234/search/output</c> or the key a save reported. The worker
    /// gets the content as its input and is told not to fetch it again, so wisp → worker becomes
    /// fetch once, filter cheaply. Use <see cref="ResolveInputKey"/> for the stored key.
    /// </summary>
    [JsonPropertyName("input_keys")]
    public IReadOnlyList<string>? InputKeys { get; init; }

    /// <summary>The prefix of a wisp step reference in <see cref="InputKeys"/>.</summary>
    public const string WispReferencePrefix = "wisp:";

    /// <summary>
    /// Resolves one <see cref="InputKeys"/> entry to its working-memory key:
    /// <c>wisp:&lt;id&gt;/&lt;step&gt;</c> → <c>wisp/&lt;id&gt;/&lt;step&gt;/output</c>; a key with a '/' is
    /// used as is. Returns null for anything else — a bare key would resolve against the worker's
    /// own, empty namespace, not the spawner's, so it is refused rather than guessed.
    /// </summary>
    public static string? ResolveInputKey(string? entry)
    {
        var key = entry?.Trim();
        if (string.IsNullOrEmpty(key))
            return null;

        if (key.StartsWith(WispReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = key[WispReferencePrefix.Length..].Trim().Trim('/');
            var slash = reference.IndexOf('/');
            if (slash <= 0 || slash == reference.Length - 1)
                return null;
            // Tolerate a reference written with the output suffix or the namespace prefix.
            if (reference.StartsWith("wisp/", StringComparison.OrdinalIgnoreCase))
                reference = reference["wisp/".Length..];
            return reference.EndsWith("/output", StringComparison.OrdinalIgnoreCase)
                ? $"wisp/{reference}"
                : $"wisp/{reference}/output";
        }

        return key.Contains('/') ? key : null;
    }

    /// <summary>
    /// Soft wall-clock cap in minutes. When null, falls back to
    /// <see cref="WorkerOptions.DefaultTimeoutMinutes"/>.
    /// </summary>
    [JsonPropertyName("timeout_minutes")]
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// Optional allowlist of tool names (exact match) or name prefixes (trailing
    /// asterisk, e.g. <c>calendar-mcp.*</c>). When non-empty, only registry tools
    /// matching the list are exposed to the worker. Applied on top of the
    /// always-exclusions enforced by the worker runner.
    /// </summary>
    [JsonPropertyName("tools_allow")]
    public IReadOnlyList<string>? ToolsAllow { get; init; }
}
