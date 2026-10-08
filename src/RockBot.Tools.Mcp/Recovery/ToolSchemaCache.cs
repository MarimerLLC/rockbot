using System.Collections.Concurrent;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Per-process cache of MCP tool schemas and prompt definitions keyed by server name. Neither
/// changes without a server reconnect, so cached entries are valid until
/// <see cref="McpServersIndexedHandler"/> invalidates the server on the next
/// <see cref="McpServersIndexed"/> message. Lookups outside the cache fetch lazily
/// through the configured delegate (typically a single bridge round-trip that returns both).
///
/// See <c>design/self-repair.md</c> Amendment 1.
/// </summary>
public sealed class ToolSchemaCache
{
    private readonly Func<string, CancellationToken, Task<McpServerSurface?>> _fetch;

    private readonly ConcurrentDictionary<string, McpServerSurface> _byServer
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A cache of tool schemas only; every server's prompt list reads as empty.</summary>
    public ToolSchemaCache(Func<string, CancellationToken, Task<IReadOnlyList<McpToolDefinition>?>> fetchServerTools)
        : this(async (server, ct) => await fetchServerTools(server, ct) is { } tools
            ? new McpServerSurface(tools, [])
            : null)
    {
    }

    private ToolSchemaCache(Func<string, CancellationToken, Task<McpServerSurface?>> fetchServerSurface)
    {
        _fetch = fetchServerSurface;
    }

    /// <summary>
    /// A cache of tool schemas and prompt definitions, fetched together (#616). A factory rather
    /// than a constructor overload, so a lambda that only throws still picks a constructor.
    /// </summary>
    public static ToolSchemaCache WithPrompts(Func<string, CancellationToken, Task<McpServerSurface?>> fetchServerSurface) =>
        new(fetchServerSurface);

    /// <summary>
    /// Returns the cached schema for the given (server, tool), or null if the
    /// fetch fails or the tool is not registered on that server.
    /// </summary>
    public async Task<McpToolDefinition?> GetAsync(string server, string tool, CancellationToken ct)
    {
        var surface = await GetSurfaceAsync(server, ct);
        return surface?.Tools.FirstOrDefault(t =>
            string.Equals(t.Name, tool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns every cached tool schema for <paramref name="server"/>, fetching on a miss, or null
    /// when the fetch fails.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDefinition>?> GetServerAsync(string server, CancellationToken ct) =>
        (await GetSurfaceAsync(server, ct))?.Tools;

    /// <summary>
    /// Returns every cached prompt definition for <paramref name="server"/>, fetching on a miss, or
    /// null when the fetch fails.
    /// </summary>
    public async Task<IReadOnlyList<McpPromptDefinition>?> GetServerPromptsAsync(string server, CancellationToken ct) =>
        (await GetSurfaceAsync(server, ct))?.Prompts;

    private async Task<McpServerSurface?> GetSurfaceAsync(string server, CancellationToken ct)
    {
        if (_byServer.TryGetValue(server, out var surface))
            return surface;

        var fetched = await _fetch(server, ct);
        if (fetched is not null)
            _byServer[server] = fetched;
        return fetched;
    }

    /// <summary>Drops cached schemas and prompts for a single server (next lookup re-fetches).</summary>
    public void Invalidate(string server) => _byServer.TryRemove(server, out _);

    /// <summary>Drops the entire cache.</summary>
    public void Clear() => _byServer.Clear();
}

/// <summary>What one MCP server exposes: its tool schemas and its prompt definitions.</summary>
public sealed record McpServerSurface(
    IReadOnlyList<McpToolDefinition> Tools,
    IReadOnlyList<McpPromptDefinition> Prompts);
