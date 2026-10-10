using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// A finished subagent's result as later work in the same conversation sees it (#665): what it was
/// asked to do, a short summary, its full output (capped) and the working-memory keys it stored.
/// </summary>
/// <param name="TaskId">The subagent's task id.</param>
/// <param name="Description">The spawn description, truncated.</param>
/// <param name="Summary">The opening of the subagent's final output.</param>
/// <param name="Output">The subagent's final output, capped at <see cref="SessionWorkRegistry.MaxOutputChars"/>.</param>
/// <param name="Keys">Working-memory keys the subagent stored, minus bulk web/tool chunk keys.</param>
/// <param name="ChunkKeyCount">How many bulk chunk/index keys were left out of <paramref name="Keys"/>.</param>
/// <param name="IsSuccess">False when the subagent failed, timed out or was cancelled.</param>
/// <param name="CompletedAt">When the result arrived.</param>
public sealed record SubagentWorkResult(
    string TaskId,
    string Description,
    string Summary,
    string Output,
    IReadOnlyList<string> Keys,
    int ChunkKeyCount,
    bool IsSuccess,
    DateTimeOffset CompletedAt)
{
    /// <summary>
    /// Builds a result entry, truncating the description, deriving the summary and splitting the
    /// stored keys into the ones worth naming and a count of bulk chunk keys.
    /// </summary>
    public static SubagentWorkResult Create(
        string taskId, string? description, string? output, IEnumerable<string> keys,
        bool isSuccess, DateTimeOffset completedAt)
    {
        var allKeys = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
        var named = allKeys.Where(k => !SessionWorkRegistry.IsBulkChunkKey(k)).ToList();
        var text = (output ?? string.Empty).Trim();
        return new SubagentWorkResult(
            taskId,
            SessionWorkRegistry.Truncate((description ?? string.Empty).Trim(), SessionWorkRegistry.MaxDescriptionChars),
            SessionWorkRegistry.Truncate(text, SessionWorkRegistry.MaxSummaryChars),
            SessionWorkRegistry.Truncate(text, SessionWorkRegistry.MaxOutputChars),
            named,
            allKeys.Count - named.Count,
            isSuccess,
            completedAt);
    }
}

/// <summary>
/// A file or remote object written during a conversation (#665): a shared-volume path written or
/// edited by the primary or one of its subagents, workers or wisps, or a remote upload target.
/// </summary>
/// <param name="Path">Shared-volume relative path, or the remote target when nothing local is known.</param>
/// <param name="LastWriterSessionId">The (normalized) session that last wrote it.</param>
/// <param name="LastTool">The tool that last wrote it.</param>
/// <param name="RemoteTarget">Where it was uploaded (<c>server:path</c> when the server is known), if it was.</param>
/// <param name="IsRemoteOnly">True when only the remote target is known (an upload with no local path).</param>
/// <param name="UpdatedAt">When it was last written.</param>
public sealed record SessionArtifact(
    string Path,
    string LastWriterSessionId,
    string LastTool,
    string? RemoteTarget,
    bool IsRemoteOnly,
    DateTimeOffset UpdatedAt);

/// <summary>What a conversation has produced so far — subagent results and artifacts, newest first.</summary>
public sealed record SessionWorkSnapshot(
    string SessionId,
    IReadOnlyList<SubagentWorkResult> Results,
    IReadOnlyList<SessionArtifact> Artifacts)
{
    /// <summary>An empty snapshot for <paramref name="sessionId"/>.</summary>
    public static SessionWorkSnapshot Empty(string sessionId) => new(sessionId, [], []);

    /// <summary>True when the conversation has no recorded results or artifacts.</summary>
    public bool IsEmpty => Results.Count == 0 && Artifacts.Count == 0;
}

/// <summary>
/// Per-conversation record of work products (#665): completed subagent results and the artifacts
/// the conversation wrote, whichever rung wrote them. Later subagents read it to see what earlier
/// ones produced, and the primary's context lists it so "the deck" resolves to a concrete path.
/// </summary>
public interface ISessionWorkRegistry
{
    /// <summary>
    /// Records that <paramref name="childSessionId"/> (a subagent, worker or wisp session) works
    /// for <paramref name="parentSessionId"/>, so its tool calls count toward the parent's conversation.
    /// </summary>
    void LinkSession(string childSessionId, string parentSessionId);

    /// <summary>The conversation (primary session) <paramref name="sessionId"/> ultimately belongs to.</summary>
    string ResolveRootSession(string sessionId);

    /// <summary>Records a finished subagent's result against <paramref name="primarySessionId"/>'s conversation.</summary>
    void RecordSubagentResult(string primarySessionId, SubagentWorkResult result);

    /// <summary>
    /// Records a finished tool call made by <paramref name="sessionId"/>. Calls that write a
    /// shared-volume file or upload to a remote target become artifacts of the conversation;
    /// everything else is ignored.
    /// </summary>
    void RecordToolCall(string sessionId, string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments, bool succeeded);

    /// <summary>The conversation's results and artifacts, newest first.</summary>
    SessionWorkSnapshot GetSnapshot(string sessionId);

    /// <summary>
    /// True when <paramref name="sessionId"/> is <paramref name="ancestorSessionId"/> or works for it
    /// through links — a subagent's wisps and workers are within the subagent (#683). The default
    /// only compares the two sessions.
    /// </summary>
    bool IsSessionWithin(string sessionId, string ancestorSessionId) =>
        SessionWorkRegistry.NormalizeSessionId(sessionId) is { Length: > 0 } s
        && s == SessionWorkRegistry.NormalizeSessionId(ancestorSessionId);
}

/// <summary>Bounds for <see cref="SessionWorkRegistry"/>.</summary>
public sealed class SessionWorkRegistryOptions
{
    /// <summary>Most recent subagent results kept per conversation.</summary>
    public int MaxResultsPerSession { get; set; } = 20;

    /// <summary>Most recently written artifacts kept per conversation.</summary>
    public int MaxArtifactsPerSession { get; set; } = 50;

    /// <summary>How long an entry is kept after it was recorded.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Conversations tracked at once; the least recently touched is dropped beyond this.</summary>
    public int MaxSessions { get; set; } = 500;
}

/// <summary>
/// In-memory, thread-safe <see cref="ISessionWorkRegistry"/>. A pod restart loses it; the working
/// memory entries and files it points at outlive it, and the conversation history still names them.
/// </summary>
public sealed partial class SessionWorkRegistry(
    SessionWorkRegistryOptions? options = null,
    TimeProvider? timeProvider = null) : ISessionWorkRegistry
{
    public const int MaxDescriptionChars = 300;
    public const int MaxSummaryChars = 600;
    public const int MaxOutputChars = 16_000;
    private const int MaxLinkDepth = 8;

    private readonly SessionWorkRegistryOptions _options = options ?? new SessionWorkRegistryOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionWork> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Parent, DateTimeOffset At)> _links = new(StringComparer.Ordinal);

    private sealed class SessionWork
    {
        public readonly List<SubagentWorkResult> Results = [];
        public readonly Dictionary<string, SessionArtifact> Artifacts = new(StringComparer.Ordinal);
        public DateTimeOffset TouchedAt;
    }

    /// <inheritdoc />
    public void LinkSession(string childSessionId, string parentSessionId)
    {
        var child = NormalizeSessionId(childSessionId);
        var parent = NormalizeSessionId(parentSessionId);
        if (child.Length == 0 || parent.Length == 0 || child == parent) return;
        lock (_lock)
        {
            _links[child] = (parent, _time.GetUtcNow());
            PruneLinks();
        }
    }

    /// <inheritdoc />
    public string ResolveRootSession(string sessionId)
    {
        lock (_lock) return ResolveRootLocked(NormalizeSessionId(sessionId));
    }

    /// <inheritdoc />
    public bool IsSessionWithin(string sessionId, string ancestorSessionId)
    {
        var current = NormalizeSessionId(sessionId);
        var ancestor = NormalizeSessionId(ancestorSessionId);
        if (current.Length == 0 || ancestor.Length == 0) return false;
        lock (_lock)
        {
            for (var depth = 0; depth <= MaxLinkDepth; depth++)
            {
                if (current == ancestor) return true;
                if (!_links.TryGetValue(current, out var link)) return false;
                current = link.Parent;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public void RecordSubagentResult(string primarySessionId, SubagentWorkResult result)
    {
        lock (_lock)
        {
            var root = ResolveRootLocked(NormalizeSessionId(primarySessionId));
            if (root.Length == 0) return;
            var work = GetOrCreateLocked(root);
            work.Results.RemoveAll(r => r.TaskId == result.TaskId);
            work.Results.Add(result);
            while (work.Results.Count > _options.MaxResultsPerSession)
                work.Results.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public void RecordToolCall(string sessionId, string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments, bool succeeded)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(toolName)) return;
        var changes = ClassifyToolCall(toolName, arguments);
        if (changes.Count == 0) return;

        lock (_lock)
        {
            var writer = NormalizeSessionId(sessionId);
            var root = ResolveRootLocked(writer);
            if (root.Length == 0) return;
            var work = GetOrCreateLocked(root);
            var now = _time.GetUtcNow();

            foreach (var change in changes)
                ApplyLocked(work, change, writer, toolName, now);

            if (work.Artifacts.Count > _options.MaxArtifactsPerSession)
            {
                foreach (var stale in work.Artifacts.Values
                             .OrderBy(a => a.UpdatedAt)
                             .Take(work.Artifacts.Count - _options.MaxArtifactsPerSession)
                             .ToList())
                    work.Artifacts.Remove(stale.Path);
            }
        }
    }

    /// <inheritdoc />
    public SessionWorkSnapshot GetSnapshot(string sessionId)
    {
        lock (_lock)
        {
            var root = ResolveRootLocked(NormalizeSessionId(sessionId));
            if (!_sessions.TryGetValue(root, out var work))
                return SessionWorkSnapshot.Empty(root);

            var cutoff = _time.GetUtcNow() - _options.Expiry;
            work.Results.RemoveAll(r => r.CompletedAt < cutoff);
            foreach (var stale in work.Artifacts.Values.Where(a => a.UpdatedAt < cutoff).ToList())
                work.Artifacts.Remove(stale.Path);

            return new SessionWorkSnapshot(
                root,
                work.Results.OrderByDescending(r => r.CompletedAt).ToList(),
                work.Artifacts.Values.OrderByDescending(a => a.UpdatedAt).ToList());
        }
    }

    private void ApplyLocked(SessionWork work, ArtifactChange change, string writer, string toolName, DateTimeOffset now)
    {
        switch (change.Kind)
        {
            case ArtifactChangeKind.Write:
                work.Artifacts.TryGetValue(change.Path, out var existing);
                work.Artifacts[change.Path] = new SessionArtifact(
                    change.Path, writer, toolName, existing?.RemoteTarget, IsRemoteOnly: false, now);
                break;

            case ArtifactChangeKind.Delete:
                work.Artifacts.Remove(change.Path);
                break;

            case ArtifactChangeKind.Move:
                work.Artifacts.Remove(change.SourcePath!, out var moved);
                work.Artifacts[change.Path] = new SessionArtifact(
                    change.Path, writer, toolName, moved?.RemoteTarget, IsRemoteOnly: false, now);
                break;

            case ArtifactChangeKind.Upload:
                if (change.SourcePath is { Length: > 0 } local)
                {
                    work.Artifacts[local] = new SessionArtifact(
                        local, writer, toolName, change.Path, IsRemoteOnly: false, now);
                }
                else
                {
                    work.Artifacts[change.Path] = new SessionArtifact(
                        change.Path, writer, toolName, change.Path, IsRemoteOnly: true, now);
                }
                break;
        }
    }

    private SessionWork GetOrCreateLocked(string root)
    {
        var now = _time.GetUtcNow();
        if (!_sessions.TryGetValue(root, out var work))
        {
            work = new SessionWork();
            _sessions[root] = work;
            if (_sessions.Count > _options.MaxSessions)
            {
                var oldest = _sessions.Where(kv => kv.Key != root).MinBy(kv => kv.Value.TouchedAt);
                if (oldest.Key is not null) _sessions.Remove(oldest.Key);
            }
        }
        work.TouchedAt = now;
        return work;
    }

    private string ResolveRootLocked(string sessionId)
    {
        var current = sessionId;
        for (var depth = 0; depth < MaxLinkDepth && _links.TryGetValue(current, out var link); depth++)
            current = link.Parent;
        return current;
    }

    private void PruneLinks()
    {
        if (_links.Count < 1000) return;
        var cutoff = _time.GetUtcNow() - _options.Expiry;
        foreach (var key in _links.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _links.Remove(key);
    }

    // ── Session id normalization ─────────────────────────────────────────────

    /// <summary>
    /// One spelling per session: a user session's working-memory namespace <c>session/{id}</c> and
    /// its raw id <c>{id}</c> are the same conversation, and a subagent appears as
    /// <c>subagent-{id}</c> (loop session), <c>subagent/{id}</c> (namespace) or <c>session/subagent-{id}</c>.
    /// </summary>
    public static string NormalizeSessionId(string? sessionId)
    {
        var s = (sessionId ?? string.Empty).Trim();
        if (s.StartsWith("session/", StringComparison.OrdinalIgnoreCase))
            s = s["session/".Length..];
        if (s.StartsWith("subagent/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["subagent/".Length..];
            var slash = rest.IndexOf('/');
            s = "subagent-" + (slash >= 0 ? rest[..slash] : rest);
        }
        return s;
    }

    // ── Tool-call classification ─────────────────────────────────────────────

    internal enum ArtifactChangeKind { Write, Delete, Move, Upload }

    /// <summary>
    /// One artifact change a tool call made. For <see cref="ArtifactChangeKind.Move"/>,
    /// <see cref="SourcePath"/> is the old path; for <see cref="ArtifactChangeKind.Upload"/>,
    /// <see cref="Path"/> is the remote target and <see cref="SourcePath"/> the local file, if known.
    /// </summary>
    internal sealed record ArtifactChange(ArtifactChangeKind Kind, string Path, string? SourcePath = null);

    private static readonly string[] RemotePathArgs =
        ["remote_path", "remotePath", "destination_path", "destination", "target_path", "target", "dest", "path", "file_name", "filename", "name"];
    private static readonly string[] LocalPathArgs =
        ["local_path", "localPath", "source_path", "source", "file_path", "local_file"];
    private static readonly string[] MoveDestinationArgs =
        ["destination", "destination_path", "new_path", "to", "target", "target_path", "dest"];
    private static readonly string[] MoveSourceArgs = ["path", "source", "source_path", "from", "old_path"];

    /// <summary>
    /// The artifact changes a call made: shared-volume <c>file_*</c> writes, edits, moves and
    /// deletes, and uploads (any effective tool name containing <c>upload</c>, unwrapped from
    /// <c>mcp_invoke_tool</c> or a typed <c>{server}__{tool}</c> name). Reads yield nothing.
    /// </summary>
    internal static IReadOnlyList<ArtifactChange> ClassifyToolCall(
        string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        // Fast path: most calls can't be an artifact write; skip flattening their arguments.
        if (!toolName.StartsWith("file_", StringComparison.OrdinalIgnoreCase)
            && !toolName.Contains("upload", StringComparison.OrdinalIgnoreCase)
            && !toolName.Contains("__", StringComparison.Ordinal)
            && !string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase))
            return [];

        var args = Flatten(arguments);
        string effective;
        string? server = null;

        if (string.Equals(toolName, ToolSideEffects.McpInvokeToolName, StringComparison.OrdinalIgnoreCase))
        {
            effective = Get(args, "tool_name") ?? string.Empty;
            server = Get(args, "server_name");
            args = Get(args, "arguments") is { } inner ? ParseJsonObject(inner) : new(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var sep = toolName.IndexOf("__", StringComparison.Ordinal);
            if (sep >= 0)
            {
                server = toolName[..sep];
                effective = toolName[(sep + 2)..];
            }
            else
            {
                effective = toolName;
            }
        }

        if (effective.Length == 0) return [];

        if (effective.Contains("upload", StringComparison.OrdinalIgnoreCase))
        {
            var local = NormalizePath(First(args, LocalPathArgs));
            var remote = First(args, RemotePathArgs);
            if (remote is null && local is null) return [];
            var target = remote ?? local!;
            if (server is { Length: > 0 }) target = $"{server}:{target}";
            return [new ArtifactChange(ArtifactChangeKind.Upload, target, local)];
        }

        // The agent's own shared-volume tools. An MCP server's file_* tool writes somewhere else.
        if (server is not null || !effective.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!ToolSideEffects.IsSideEffecting(effective)) return [];

        var words = ToolSideEffects.SplitWords(effective);
        if (words.Contains("delete") || words.Contains("remove"))
            return NormalizePath(Get(args, "path")) is { } deleted
                ? [new ArtifactChange(ArtifactChangeKind.Delete, deleted)]
                : [];

        if (words.Contains("move") || words.Contains("rename") || words.Contains("copy"))
        {
            var destination = NormalizePath(First(args, MoveDestinationArgs));
            var source = NormalizePath(First(args, MoveSourceArgs.Where(a => !MoveDestinationArgs.Contains(a)).ToArray()));
            if (destination is null) return [];
            return words.Contains("copy") || source is null
                ? [new ArtifactChange(ArtifactChangeKind.Write, destination)]
                : [new ArtifactChange(ArtifactChangeKind.Move, destination, source)];
        }

        return NormalizePath(Get(args, "path")) is { } written
            ? [new ArtifactChange(ArtifactChangeKind.Write, written)]
            : [];
    }

    private static Dictionary<string, string> Flatten(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments is null) return dict;
        foreach (var (key, value) in arguments)
        {
            var text = ValueToString(value);
            if (text is not null) dict[key] = text;
        }
        return dict;
    }

    private static string? ValueToString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement e => e.GetRawText(),
        System.Text.Json.Nodes.JsonNode n => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var sv) ? sv : n.ToJsonString(),
        System.Collections.IDictionary or System.Collections.IEnumerable and not string => SerializeOrNull(value),
        _ => value.ToString(),
    };

    private static string? SerializeOrNull(object value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch { return value.ToString(); }
    }

    /// <summary>Parses a JSON object argument string into a flat string dictionary; empty on failure.</summary>
    internal static Dictionary<string, string> ParseJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(StringComparer.OrdinalIgnoreCase);
            return Flatten(doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, object?>(p.Name, p.Value.Clone())));
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Parses a tool call's JSON argument string for <see cref="ISessionWorkRegistry.RecordToolCall"/>.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> ParseArguments(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : ParseJsonObject(json).Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList();

    private static string? Get(Dictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string? First(Dictionary<string, string> args, string[] names)
    {
        foreach (var name in names)
            if (Get(args, name) is { } v) return v;
        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.TrimStart('/');
        return p.Length == 0 ? null : p;
    }

    // ── Helpers shared with the lineage and context renderers ────────────────

    /// <summary>
    /// True for bulk keys a web browse or chunked tool result stores (<c>…-chunk3</c>,
    /// <c>…-index</c>): worth counting, not worth naming one by one.
    /// </summary>
    public static bool IsBulkChunkKey(string key) => BulkChunkKeyRegex().IsMatch(key);

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    [GeneratedRegex(@"-(chunk\d+|index)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BulkChunkKeyRegex();
}
