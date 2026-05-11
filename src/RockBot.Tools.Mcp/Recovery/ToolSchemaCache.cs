using System.Collections.Concurrent;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Per-process cache of MCP tool schemas keyed by server name. Schemas don't change
/// without a server reconnect, so cached entries are valid until
/// <see cref="McpServersIndexedHandler"/> invalidates the server on the next
/// <see cref="McpServersIndexed"/> message. Lookups outside the cache fetch lazily
/// through the configured delegate (typically a single bridge round-trip).
///
/// See <c>design/self-repair.md</c> Amendment 1.
/// </summary>
public sealed class ToolSchemaCache(
    Func<string, CancellationToken, Task<IReadOnlyList<McpToolDefinition>?>> fetchServerTools)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpToolDefinition>> _byServer
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached schema for the given (server, tool), or null if the
    /// fetch fails or the tool is not registered on that server.
    /// </summary>
    public async Task<McpToolDefinition?> GetAsync(string server, string tool, CancellationToken ct)
    {
        if (!_byServer.TryGetValue(server, out var tools))
        {
            var fetched = await fetchServerTools(server, ct);
            if (fetched is null) return null;
            tools = fetched;
            _byServer[server] = tools;
        }
        return tools.FirstOrDefault(t =>
            string.Equals(t.Name, tool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drops cached schemas for a single server (next lookup re-fetches).</summary>
    public void Invalidate(string server) => _byServer.TryRemove(server, out _);

    /// <summary>Drops the entire cache.</summary>
    public void Clear() => _byServer.Clear();
}
