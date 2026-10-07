using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// The typed MCP tool surface as the agent loop sees it, and, in <see cref="McpWrapperMode.Lazy"/>,
/// the per-session activations (#612, porting mcp-aggregator#42's lazy mode).
/// <para>
/// Activations are scoped to one tool session — a conversation, a subagent run, a worker run —
/// and never registered in the global <see cref="IToolRegistry"/>: one session's search must not
/// grow every other session's context. A session keeps at most
/// <see cref="McpToolSurfaceOptions.MaxActivatedToolsPerSession"/> (oldest dropped first) and
/// loses them all after <see cref="McpToolSurfaceOptions.ActivationIdleTimeout"/> unused. A
/// tool whose server goes away or whose surface changes is evicted everywhere; the next search
/// activates it again with its new schema.
/// </para>
/// <para>
/// Deliberately dependency-free so the agent loop can take it without a DI cycle;
/// <see cref="McpWrapperCatalog"/> binds itself here when it is built.
/// </para>
/// </summary>
public sealed class McpTypedToolSurface : ITypedToolSurface
{
    public const string FindToolsName = "mcp_find_tools";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly McpToolSurfaceOptions _options;
    private readonly ILogger<McpTypedToolSurface> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private McpWrapperCatalog? _catalog;
    private long _lastSweepTicks;

    public McpTypedToolSurface(
        IOptions<McpToolSurfaceOptions> options,
        ILogger<McpTypedToolSurface> logger,
        TimeProvider? time = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastSweepTicks = _time.GetUtcNow().UtcTicks;
    }

    public TypedToolMode Mode => _options.WrapperMode switch
    {
        McpWrapperMode.Eager => TypedToolMode.Eager,
        McpWrapperMode.Lazy => TypedToolMode.Lazy,
        _ => TypedToolMode.Off
    };

    public string LoaderToolName => FindToolsName;

    internal void Bind(McpWrapperCatalog catalog) => _catalog = catalog;

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>Typed tools ranked for <paramref name="query"/> (<see cref="McpToolSearch"/>).</summary>
    public IReadOnlyList<McpToolMatch> Find(string query, IReadOnlyList<McpServerSummary> servers, int limit) =>
        _catalog is { } catalog
            ? McpToolSearch.Rank(query, catalog.Wrappers, servers, limit)
            : [];

    /// <summary>
    /// The typed names of <paramref name="server"/>'s tools that best fit <paramref name="query"/>,
    /// falling back to its first tools when none match. Empty when typed tools are off.
    /// </summary>
    public IReadOnlyList<string> TypedNamesFor(McpServerSummary server, string query, int max)
    {
        if (Mode == TypedToolMode.Off || _catalog is not { } catalog)
            return [];

        var tools = catalog.WrappersFor(server.ServerName);
        var ranked = McpToolSearch.Rank(query, tools, [server], max).Select(m => m.Tool.Name).ToList();
        return ranked.Count > 0
            ? ranked
            : tools.OrderBy(t => server.ToolNames.IndexOf(t.ToolName)).Take(max).Select(t => t.Name).ToList();
    }

    /// <summary>The typed tools for <paramref name="server"/>, or for its one tool <paramref name="toolName"/>.</summary>
    public IReadOnlyList<McpWrapperTool> WrappersFor(string server, string? toolName = null)
    {
        if (_catalog is not { } catalog)
            return [];

        var tools = catalog.WrappersFor(server);
        return toolName is null
            ? tools
            : tools.Where(t => string.Equals(t.ToolName, toolName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ── Activation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Activates <paramref name="tools"/> for <paramref name="sessionId"/>, in order. Returns the
    /// names now active; empty outside lazy mode or without a session.
    /// </summary>
    public IReadOnlyList<string> Activate(string? sessionId, IEnumerable<McpWrapperTool> tools)
    {
        if (Mode != TypedToolMode.Lazy || string.IsNullOrEmpty(sessionId))
            return [];

        SweepIfDue();

        var requested = tools.ToList();
        var cap = Math.Max(1, _options.MaxActivatedToolsPerSession);
        var session = _sessions.GetOrAdd(sessionId, _ => new Session { Touched = _time.GetUtcNow() });
        var added = new List<string>();
        lock (session)
        {
            session.Touched = _time.GetUtcNow();
            foreach (var tool in requested)
            {
                // Re-activating keeps a tool's place: moving it would reorder the tool list and
                // cost the provider's prompt cache for nothing.
                if (!session.Names.Contains(tool.Name))
                {
                    session.Names.Add(tool.Name);
                    added.Add(tool.Name);
                }
            }

            var overflow = session.Names.Count - cap;
            if (overflow > 0)
            {
                _logger.LogInformation(
                    "Typed MCP tools for session {Session}: dropping {Count} oldest activation(s) to stay at {Cap}",
                    sessionId, overflow, cap);
                session.Names.RemoveRange(0, overflow);
            }

            if (added.Count > 0)
            {
                _logger.LogInformation("Typed MCP tools activated for session {Session}: {Tools}",
                    sessionId, string.Join(", ", added));
            }

            return [.. requested.Select(t => t.Name).Where(session.Names.Contains)];
        }
    }

    /// <summary>True when <paramref name="toolName"/> is active for <paramref name="sessionId"/>.</summary>
    public bool IsActivated(string sessionId, string toolName)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        lock (session) return session.Names.Contains(toolName);
    }

    public IReadOnlyList<AIFunction> GetActivated(string toolSessionId)
    {
        if (Mode != TypedToolMode.Lazy || _catalog is not { } catalog
            || !_sessions.TryGetValue(toolSessionId, out var session))
            return [];

        List<string> names;
        lock (session)
        {
            session.Touched = _time.GetUtcNow();
            names = [.. session.Names];
        }

        var tools = new List<AIFunction>(names.Count);
        foreach (var name in names)
        {
            if (catalog.TryGet(name, out var wrapper))
                tools.Add(catalog.CreateFunction(wrapper, toolSessionId));
        }
        return tools;
    }

    public AIFunction? ActivateByName(string toolSessionId, string toolName)
    {
        if (Mode != TypedToolMode.Lazy || _catalog is not { } catalog || !catalog.TryGet(toolName, out var wrapper))
            return null;

        _logger.LogInformation("Typed MCP tool {Tool} called by name in session {Session}; activating it",
            toolName, toolSessionId);
        Activate(toolSessionId, [wrapper]);
        return catalog.CreateFunction(wrapper, toolSessionId);
    }

    /// <summary>Removes <paramref name="toolNames"/> from every session.</summary>
    internal void Evict(IReadOnlySet<string> toolNames)
    {
        if (toolNames.Count == 0)
            return;

        var evicted = 0;
        foreach (var session in _sessions.Values)
        {
            lock (session)
                evicted += session.Names.RemoveAll(toolNames.Contains);
        }

        if (evicted > 0)
        {
            _logger.LogInformation(
                "Evicted {Count} typed MCP tool activation(s) whose server was removed or changed: {Tools}",
                evicted, string.Join(", ", toolNames));
        }
    }

    private void SweepIfDue()
    {
        var now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
            return;

        foreach (var (id, session) in _sessions)
        {
            bool idle;
            lock (session) idle = now - session.Touched > _options.ActivationIdleTimeout;
            if (idle)
                _sessions.TryRemove(id, out _);
        }
    }

    private sealed class Session
    {
        public List<string> Names { get; } = [];
        public DateTimeOffset Touched { get; set; }
    }
}
