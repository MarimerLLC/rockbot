using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// The read-only fetches one agent run has already made, keyed by tool and arguments (#847). A
/// subagent once ran 42 email searches in a wisp, then had workers run the same 42 again so a cheap
/// model could filter them, and then called <c>get_email_details</c> itself for bodies the wisp had
/// already fetched. This registry lets the tool-calling paths spot a call that repeats an earlier
/// successful fetch and tell the model where that data already is.
/// <para>
/// Recorded by the loop's own tool calls and by wisp Direct steps. Workers spawned from the run share
/// it (<see cref="AgentLoopRunner.RunAsync"/> takes it as a parameter), so a worker brief that repeats
/// a wisp's fetch is flagged too. Only reads are kept: a successful state-changing call on a server
/// forgets that server's reads, because re-reading after a write is legitimate. Reads expected to
/// change between calls (status polls, clocks) and MCP discovery are not tracked, and an earlier
/// fetch is only cited while the caller can still reach its data. Thread-safe.
/// </para>
/// </summary>
public sealed class RunFetchRegistry
{
    /// <summary>Where an earlier fetch's data lives.</summary>
    /// <param name="Tool">The tool's display name, e.g. <c>calendar-mcp__get_calendar_events</c>.</param>
    /// <param name="Location">Who made the call, e.g. <c>by wisp wisp-1234 step list</c>.</param>
    /// <param name="Key">The working-memory key holding its output, when there is one.</param>
    public sealed record Entry(string Tool, string Location, string? Key)
    {
        /// <summary>
        /// The loop (session id) whose conversation holds the result, for a call recorded without a
        /// <see cref="Key"/>. Its result is visible only there, so a repeat from another loop sharing
        /// this registry — a worker, a wisp step, the spawner after its workers return — is not told
        /// the result is "already in this conversation".
        /// </summary>
        public string? Origin { get; init; }

        /// <summary>When <see cref="Key"/> expires from working memory, or null when unknown.</summary>
        public DateTimeOffset? KeyExpiresAt { get; init; }
    }

    private readonly Dictionary<string, (string Server, Entry Entry)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    /// <summary>A registry using the system clock.</summary>
    public RunFetchRegistry() : this(TimeProvider.System) { }

    /// <summary>A registry using <paramref name="time"/> to age working-memory keys.</summary>
    public RunFetchRegistry(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Number of fetches recorded.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// Records a finished call and returns the earlier fetch it repeats, or null. A failed call
    /// changes nothing. A successful read is remembered (the first location is kept); a successful
    /// state-changing call forgets the reads of its server.
    /// <para>
    /// An earlier fetch is only reported when its data can still be reached by the caller: one
    /// stored under a key that has not expired, or one made by the same loop
    /// (<paramref name="origin"/>), whose result is in that loop's conversation. Otherwise the new
    /// call replaces it and nothing is reported, so the note never sends the model to data it
    /// cannot read.
    /// </para>
    /// </summary>
    /// <param name="toolName">The tool name as called.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="succeeded">Whether the call succeeded.</param>
    /// <param name="location">Who made the call, for a later duplicate's note.</param>
    /// <param name="key">Working-memory key holding the output, or null.</param>
    /// <param name="origin">The loop (session id) making the call; compared for calls with no key.</param>
    /// <param name="keyLifetime">How long <paramref name="key"/> stays in working memory, or null when unknown.</param>
    public Entry? Observe(
        string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        bool succeeded,
        string location,
        string? key = null,
        string? origin = null,
        TimeSpan? keyLifetime = null)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(toolName)) return null;
        var call = Describe(toolName, arguments);
        if (call is null) return null;
        return Observe(call.Value, location, key, origin, keyLifetime);
    }

    /// <summary>
    /// <see cref="Observe(string, IEnumerable{KeyValuePair{string, object?}}?, bool, string, string?, string?, TimeSpan?)"/>
    /// for arguments given as a JSON object string (a wisp step's routed arguments).
    /// </summary>
    public Entry? Observe(string toolName, string? argumentsJson, bool succeeded, string location,
        string? key = null, string? origin = null, TimeSpan? keyLifetime = null)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(toolName)) return null;
        var call = Describe(toolName, ParseArguments(argumentsJson));
        if (call is null) return null;
        return Observe(call.Value, location, key, origin, keyLifetime);
    }

    private Entry? Observe(CallIdentity call, string location, string? key, string? origin, TimeSpan? keyLifetime)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (call.SideEffecting)
            {
                foreach (var stale in _entries.Where(e => e.Value.Server == call.Server).Select(e => e.Key).ToList())
                    _entries.Remove(stale);
                return null;
            }

            if (_entries.TryGetValue(call.Fingerprint, out var prior) && IsReachable(prior.Entry, origin, now))
                return prior.Entry;

            // First fetch, or the earlier one's data is out of the caller's reach: this call is the
            // one a later repeat should be pointed at.
            _entries[call.Fingerprint] = (call.Server, new Entry(call.DisplayName, location, key)
            {
                Origin = origin,
                KeyExpiresAt = key is not null && keyLifetime is { } life ? now + life : null,
            });
            return null;
        }
    }

    private static bool IsReachable(Entry entry, string? origin, DateTimeOffset now) =>
        entry.Key is { Length: > 0 }
            ? entry.KeyExpiresAt is not { } expires || now < expires
            : string.Equals(entry.Origin, origin, StringComparison.Ordinal);

    /// <summary>
    /// The note appended to a call's result when it repeats <paramref name="prior"/>. One wording
    /// for every tool-calling path. It discourages a repeat rather than forbidding one: a deliberate
    /// re-read for fresher data is still the caller's call.
    /// </summary>
    public static string FormatDuplicateNote(Entry prior)
    {
        var where = prior.Key is { Length: > 0 } key
            ? $" Its output is in working memory at `{key}`: read it with get_from_working_memory " +
              "(or, if spawn_workers is in your tool list, pass the key to it as input_keys so a worker filters it)."
            : " Its result is already earlier in this conversation (or in the stash it was moved to).";
        return $"\n\n[System note: this exact call ({prior.Tool}, same arguments) already succeeded in this run, made {prior.Location}.{where} " +
               "Use that instead of fetching the same data again, and don't ask a worker to re-run it. " +
               "Calling again is fine only when you need fresher data, e.g. a status that may have changed.]";
    }

    private readonly record struct CallIdentity(string Fingerprint, string Server, string DisplayName, bool SideEffecting);

    /// <summary>
    /// The call's identity: the downstream tool (an <c>mcp_invoke_tool</c> call and the matching typed
    /// <c>{server}__{tool}</c> call compare equal) and its arguments with object keys sorted. Null for
    /// calls that are not fetches worth tracking: the agent's own bookkeeping and context reads,
    /// delegation and batch tools, and an <c>mcp_invoke_tool</c> call missing its target.
    /// </summary>
    private static CallIdentity? Describe(string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        string server, tool;
        JsonNode? args;

        if (string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase))
        {
            var map = arguments?.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
                      ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            // Sanitized as a typed wrapper name is, so this call and the typed {server}__{tool}
            // call compare equal even for a server such as "mail.prod" (wrapper "mail-prod__…").
            server = SanitizeWrapperPart(AsString(map.GetValueOrDefault("server_name")));
            tool = SanitizeWrapperPart(AsString(map.GetValueOrDefault("tool_name")));
            if (server.Length == 0 || tool.Length == 0) return null;
            args = ToNode(map.GetValueOrDefault("arguments"));
            // A model sometimes sends the inner arguments as a JSON string.
            if (args is JsonValue v && v.TryGetValue<string>(out var s) && TryParseNode(s) is { } parsed)
                args = parsed;
        }
        else if (McpServerScopedGatewayTools.Contains(toolName))
        {
            // mcp_read_resource / mcp_get_prompt name their server in server_name. Keep it, so a
            // write on that server forgets these reads too; the rest of the arguments identify them.
            var map = arguments?.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
                      ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            server = SanitizeWrapperPart(AsString(map.GetValueOrDefault("server_name")));
            if (server.Length == 0) return null;
            map.Remove("server_name");
            tool = toolName;
            args = ToNode(map);
        }
        else
        {
            var sep = toolName.IndexOf("__", StringComparison.Ordinal);
            server = sep > 0 ? toolName[..sep] : string.Empty;
            tool = sep > 0 ? toolName[(sep + 2)..] : toolName;
            args = ToNode(arguments?.ToDictionary(a => a.Key, a => a.Value));
        }

        // A tool named like the agent's own bookkeeping (task_update, save_memory, …) is a write when
        // it runs on an MCP server: it changes that server's data, so its reads must be forgotten.
        var sideEffecting = ToolSideEffects.IsSideEffectingName(tool)
                            || (server.Length > 0 && ToolSideEffects.IsBookkeeping(tool));
        if (!sideEffecting && !IsTrackedRead(toolName, tool)) return null;

        var display = McpServerScopedGatewayTools.Contains(toolName)
            ? $"{toolName} on {server}"
            : server.Length > 0 ? $"{server}__{tool}" : tool;
        var canonical = Canonicalize(args)?.ToJsonString() ?? "{}";
        if (canonical == "null") canonical = "{}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var fingerprint = $"{server.ToLowerInvariant()}|{tool.ToLowerInvariant()}|{hash}";
        return new CallIdentity(fingerprint, server.ToLowerInvariant(), display, sideEffecting);
    }

    /// <summary>
    /// Words that mark a read whose answer is expected to change between calls — a status poll, a
    /// clock, a health check. Repeating one is the point, so it is never flagged.
    /// </summary>
    private static readonly HashSet<string> VolatileReadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "poll", "progress", "health", "heartbeat", "current", "now", "time", "wait",
    };

    /// <summary>
    /// MCP gateway tools that fetch real data. The others (<c>mcp_list_services</c>,
    /// <c>mcp_get_service_details</c>, <c>mcp_find_tools</c>, …) are discovery, which the directives
    /// tell the agent to repeat after a failure, so they are not tracked.
    /// </summary>
    private static readonly HashSet<string> McpDataGatewayTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ToolSideEffects.McpInvokeToolName, "mcp_read_resource", "mcp_get_prompt",
    };

    /// <summary>Gateway tools that read from the server named in their <c>server_name</c> argument.</summary>
    private static readonly HashSet<string> McpServerScopedGatewayTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcp_read_resource", "mcp_get_prompt",
    };

    /// <summary>
    /// One part of a typed wrapper name as <c>McpWrapperNaming.For</c> writes it: every character
    /// outside <c>[A-Za-z0-9_-]</c> becomes <c>-</c>. Applied to the server and tool an
    /// <c>mcp_invoke_tool</c> call names, so they key like the typed call's parts. Two servers whose
    /// names differ only in such a character would share entries; that only costs a stray note.
    /// </summary>
    internal static string SanitizeWrapperPart(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.All(IsWrapperChar)
            ? value
            : new string(value.Select(c => IsWrapperChar(c) ? c : '-').ToArray());

        static bool IsWrapperChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
    }

    private static bool IsTrackedRead(string calledName, string tool)
    {
        // Batch and delegation tools: the calls they make are recorded where they run.
        if (calledName.StartsWith("spawn_", StringComparison.OrdinalIgnoreCase)) return false;
        if (ToolSideEffects.IsDelegation(calledName)) return false;
        if (calledName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) && !McpDataGatewayTools.Contains(calledName))
            return false;
        if (ToolSideEffects.SplitWords(tool).Any(VolatileReadWords.Contains)) return false;
        // Bookkeeping, context reads (get_from_working_memory is what the note tells the model to
        // call) and instant utilities are not fetches.
        return ToolSideEffects.IsThreadWork(new LoopToolCall(tool, null, true));
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.ToString(),
        _ => value.ToString(),
    };

    private static IEnumerable<KeyValuePair<string, object?>>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, object?>(p.Name, p.Value.Clone()))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        try
        {
            return value switch
            {
                JsonNode n => n.DeepClone(),
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                _ => JsonSerializer.SerializeToNode(value),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return JsonValue.Create(value.ToString());
        }
    }

    private static JsonNode? TryParseNode(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    // Object keys sorted (ordinal) at every level, so key order never makes two calls differ.
    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Canonicalize).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}

/// <summary>
/// Ambient per-async-flow handle to the <see cref="RunFetchRegistry"/> of the running loop (#847).
/// Bound by <see cref="AgentLoopRunner.RunAsync"/>; read by the tool-calling paths and by wisp Direct
/// steps, which run inside the parent's <c>spawn_wisps</c> call.
/// </summary>
public static class RunFetchRegistryContext
{
    private static readonly AsyncLocal<RunFetchRegistry?> Current = new();

    /// <summary>The registry of the loop running on this async flow, or null outside a loop.</summary>
    public static RunFetchRegistry? Value => Current.Value;

    /// <summary>Binds <paramref name="registry"/> to the current async flow until the scope is disposed.</summary>
    public static IDisposable Set(RunFetchRegistry? registry)
    {
        var previous = Current.Value;
        Current.Value = registry;
        return new Scope(previous);
    }

    /// <summary>
    /// Records a finished tool call of the running loop and returns the note to append to its result
    /// when it repeats an earlier fetch, or null. Never throws: observation must not fail a call.
    /// </summary>
    public static string? ObserveLoopCall(
        string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments, bool succeeded)
    {
        if (Current.Value is not { } registry) return null;
        try
        {
            // The loop's session id: a call with no key is reported only to the loop that holds its result.
            var prior = registry.Observe(toolName, arguments, succeeded, "by an earlier direct call",
                origin: ToolCallSessionContext.SessionId ?? string.Empty);
            return prior is null ? null : RunFetchRegistry.FormatDuplicateNote(prior);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Scope(RunFetchRegistry? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
