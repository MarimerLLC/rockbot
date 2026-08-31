namespace RockBot.Tools.Mcp;

/// <summary>
/// Thread-safe in-memory cache of MCP server summaries.
/// Updated by <see cref="McpServersIndexedHandler"/> whenever the bridge publishes
/// a new index. Used by <c>mcp_list_services</c> without a bridge round-trip.
/// </summary>
public sealed class McpServerIndex
{
    private readonly object _lock = new();
    private List<McpServerSummary> _servers = [];

    /// <summary>
    /// Whether the 5 management tools have been registered in <see cref="IToolRegistry"/>.
    /// Stored here (singleton) because the handler is scoped (created per message).
    /// </summary>
    public bool ManagementToolsRegistered { get; set; }

    public IReadOnlyList<McpServerSummary> Servers
    {
        get { lock (_lock) { return _servers; } }
    }

    /// <summary>
    /// Applies an index update: adds/updates servers in <see cref="McpServersIndexed.Servers"/>
    /// and removes any servers listed in <see cref="McpServersIndexed.RemovedServers"/>.
    /// </summary>
    public void Apply(McpServersIndexed message)
    {
        lock (_lock)
        {
            if (message.RemovedServers.Count > 0)
            {
                var removed = new HashSet<string>(message.RemovedServers, StringComparer.OrdinalIgnoreCase);
                _servers = _servers.Where(s => !removed.Contains(s.ServerName)).ToList();
            }

            foreach (var incoming in message.Servers)
            {
                var idx = _servers.FindIndex(s =>
                    string.Equals(s.ServerName, incoming.ServerName, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                    _servers[idx] = incoming;
                else
                    _servers.Add(incoming);
            }
        }
    }
}
