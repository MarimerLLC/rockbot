using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>Where a <see cref="SubagentInput"/> came from.</summary>
public enum SubagentInputKind
{
    /// <summary>A working-memory entry.</summary>
    WorkingMemory,

    /// <summary>A shared-volume file.</summary>
    File,
}

/// <summary>
/// A working-memory key or shared-volume file the primary told a subagent to use (#665, the
/// <c>spawn_subagent</c> <c>inputs</c> parameter), resolved and read at spawn time.
/// </summary>
/// <param name="Reference">The exact key or path that resolved.</param>
/// <param name="Kind">Working memory or file.</param>
/// <param name="Content">Its content when the subagent was spawned.</param>
public sealed record SubagentInput(string Reference, SubagentInputKind Kind, string Content);

/// <summary>The resolved inputs, or the error to return instead of spawning.</summary>
internal sealed record SubagentInputResolution(IReadOnlyList<SubagentInput> Inputs, string? Error)
{
    public static SubagentInputResolution None { get; } = new([], null);
}

/// <summary>
/// Resolves <c>spawn_subagent</c> <c>inputs</c> (#665). Each reference must name an existing
/// working-memory key (absolute, or a plain key in the caller's own namespace) or a shared-volume
/// file (read through the registered <c>file_read</c> tool). A reference that names neither fails
/// the spawn with the close matches that do exist, so a wrong key fails fast instead of being
/// invented later by the subagent.
/// </summary>
internal sealed class SubagentInputResolver(IWorkingMemory workingMemory, IToolRegistry? toolRegistry)
{
    /// <summary>Longest input content kept; the lineage budget inlines less than this.</summary>
    internal const int MaxInputChars = 100_000;

    private const int MaxInputs = 10;
    private const int MaxSuggestions = 5;

    public async Task<SubagentInputResolution> ResolveAsync(
        IReadOnlyList<string> references, string? callerNamespace, CancellationToken ct)
    {
        var refs = references
            .Select(r => r?.Trim() ?? string.Empty)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (refs.Count == 0) return SubagentInputResolution.None;
        if (refs.Count > MaxInputs)
            return new([], $"spawn_subagent accepts at most {MaxInputs} inputs; {refs.Count} were given. No subagent was spawned.");

        var resolved = new List<SubagentInput>();
        var missing = new List<string>();
        foreach (var reference in refs)
        {
            var input = await TryResolveAsync(reference, callerNamespace, ct);
            if (input is not null)
            {
                resolved.Add(input);
                continue;
            }

            var suggestions = await SuggestAsync(reference, callerNamespace, ct);
            missing.Add(suggestions.Count > 0
                ? $"'{reference}' (close matches: {string.Join(", ", suggestions.Select(s => $"'{s}'"))})"
                : $"'{reference}' (no close matches)");
        }

        if (missing.Count > 0)
            return new([],
                $"spawn_subagent inputs not found, so no subagent was spawned: {string.Join("; ", missing)}. " +
                "Each input must be an existing working-memory key (e.g. 'subagent/<task-id>/<key>') or " +
                "shared-volume file path (e.g. 'drafts/deck.md'). Use the exact key or path, or leave it out.");

        return new(resolved, null);
    }

    private async Task<SubagentInput?> TryResolveAsync(string reference, string? callerNamespace, CancellationToken ct)
    {
        foreach (var key in CandidateKeys(reference, callerNamespace))
        {
            var value = await workingMemory.GetAsync(key);
            if (value is not null)
                return new SubagentInput(key, SubagentInputKind.WorkingMemory, Cap(value));
        }

        var content = await TryReadFileAsync(reference, ct);
        return content is null ? null : new SubagentInput(NormalizePath(reference), SubagentInputKind.File, Cap(content));
    }

    private static IEnumerable<string> CandidateKeys(string reference, string? callerNamespace)
    {
        yield return reference;
        if (!reference.Contains('/') && !string.IsNullOrWhiteSpace(callerNamespace))
            yield return $"{callerNamespace.TrimEnd('/')}/{reference}";
    }

    private async Task<string?> TryReadFileAsync(string path, CancellationToken ct)
    {
        var executor = toolRegistry?.GetExecutor("file_read");
        if (executor is null) return null;
        try
        {
            var response = await executor.ExecuteAsync(new ToolInvokeRequest
            {
                ToolCallId = $"spawn-input-{Guid.NewGuid():N}"[..24],
                ToolName = "file_read",
                Arguments = JsonSerializer.Serialize(new { path = NormalizePath(path) }),
            }, ct);
            return response.IsError ? null : response.Content ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keys in the same namespace and same-named keys elsewhere, then files with the same name or
    /// in the same folder.
    /// </summary>
    private async Task<IReadOnlyList<string>> SuggestAsync(string reference, string? callerNamespace, CancellationToken ct)
    {
        var suggestions = new List<string>();
        var normalized = reference.Trim().Trim('/');
        var slash = normalized.LastIndexOf('/');
        var leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var parent = slash >= 0 ? normalized[..slash] : callerNamespace?.TrimEnd('/');
        var top = normalized.Split('/')[0];

        try
        {
            if (!string.IsNullOrEmpty(parent))
            {
                suggestions.AddRange((await workingMemory.ListAsync(parent + "/"))
                    .Where(e => !SessionWorkRegistry.IsBulkChunkKey(e.Key))
                    .OrderByDescending(e => Similar(LeafOf(e.Key), leaf))
                    .ThenByDescending(e => e.StoredAt)
                    .Select(e => e.Key)
                    .Take(MaxSuggestions));
            }

            // A key copied from another task: same leaf name under a different namespace.
            var wider = new List<WorkingMemoryEntry>();
            if (slash >= 0 && top.Length > 0)
                wider.AddRange(await workingMemory.ListAsync(top + "/"));
            if (!string.IsNullOrWhiteSpace(callerNamespace))
                wider.AddRange(await workingMemory.ListAsync(callerNamespace.TrimEnd('/') + "/"));
            suggestions.AddRange(wider
                .Where(e => !SessionWorkRegistry.IsBulkChunkKey(e.Key) && Similar(LeafOf(e.Key), leaf))
                .OrderByDescending(e => e.StoredAt)
                .Select(e => e.Key));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions are best-effort.
        }

        suggestions.AddRange(await SuggestFilesAsync(normalized, ct));

        return suggestions
            .Where(s => !string.Equals(s, reference, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .ToList();
    }

    private async Task<IEnumerable<string>> SuggestFilesAsync(string path, CancellationToken ct)
    {
        var executor = toolRegistry?.GetExecutor("file_list");
        if (executor is null) return [];
        try
        {
            var response = await executor.ExecuteAsync(new ToolInvokeRequest
            {
                ToolCallId = $"spawn-input-{Guid.NewGuid():N}"[..24],
                ToolName = "file_list",
                Arguments = "{}",
            }, ct);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content)) return [];
            var files = JsonSerializer.Deserialize<List<string>>(response.Content) ?? [];
            var name = Path.GetFileName(path);
            var nameNoExt = Path.GetFileNameWithoutExtension(path);
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            return files
                .Where(f =>
                    string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)
                    || (nameNoExt.Length >= 4 && Path.GetFileName(f).Contains(nameNoExt, StringComparison.OrdinalIgnoreCase))
                    || (dir.Length > 0 && string.Equals(Path.GetDirectoryName(f)?.Replace('\\', '/'), dir, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSuggestions)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static string LeafOf(string key)
    {
        var i = key.LastIndexOf('/');
        return i >= 0 ? key[(i + 1)..] : key;
    }

    private static bool Similar(string a, string b) =>
        a.Length > 0 && b.Length > 0
        && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    private static string Cap(string content) =>
        content.Length <= MaxInputChars ? content : content[..MaxInputChars];
}
