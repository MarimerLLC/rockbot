using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>One typed wrapper: the registered name and the downstream tool it stands for.</summary>
public sealed record McpWrapperTool(
    string Name,
    string ServerName,
    string? ServerId,
    string ToolName,
    string? Description,
    string? InputSchema,
    string Fingerprint);

/// <summary>
/// Keeps the typed <c>{server}__{tool}</c> wrapper tools in step with the bridge's server index
/// (#420, porting mcp-aggregator#42). In <see cref="McpWrapperMode.Eager"/> they are registered in
/// the <see cref="IToolRegistry"/>; in <see cref="McpWrapperMode.Lazy"/> they are only indexed here,
/// and <see cref="McpTypedToolSurface"/> adds them to the sessions that activate them (#612).
/// <para>
/// On each <see cref="McpServersIndexed"/> it reconciles the affected servers' wrappers: tools
/// that appeared are registered, tools that vanished or whose fingerprint (description plus
/// canonical schema) moved are replaced, and an unchanged tool keeps its registration, so a
/// re-published index doesn't churn the tool list. Each wrapper's parameter schema is the
/// downstream input schema, unchanged.
/// </para>
/// <para>
/// Collision rules: the server prefix keeps tools with the same name on different servers apart
/// (both OneDrive servers' <c>list_files</c>); two tools on one server that sanitise to the same
/// name keep the first; a wrapper never displaces a tool registered by anything else; and a name
/// over the providers' 64-character limit is not registered — the tool stays reachable through
/// <c>mcp_invoke_tool</c>.
/// </para>
/// </summary>
public sealed class McpWrapperCatalog
{
    private readonly IToolRegistry _registry;
    private readonly ToolSchemaCache _schemas;
    private readonly McpWrapperToolExecutor _executor;
    private readonly McpToolSurfaceOptions _options;
    private readonly McpTypedToolSurface _surface;
    private readonly ILogger<McpWrapperCatalog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Guarded by _gate for writes; _byName is read lock-free by the executor, so it is swapped
    // whole rather than mutated.
    private readonly Dictionary<string, Dictionary<string, McpWrapperTool>> _byServer = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, McpWrapperTool> _byName = new Dictionary<string, McpWrapperTool>(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedSkips = new(StringComparer.Ordinal);

    public McpWrapperCatalog(
        IToolRegistry registry,
        ToolSchemaCache schemas,
        McpManagementExecutor management,
        IOptions<McpToolSurfaceOptions> options,
        ILogger<McpWrapperCatalog> logger,
        McpTypedToolSurface? surface = null)
    {
        _registry = registry;
        _schemas = schemas;
        _options = options.Value;
        _logger = logger;
        _executor = new McpWrapperToolExecutor(this, management);
        _surface = surface ?? new McpTypedToolSurface(options, NullLogger<McpTypedToolSurface>.Instance);
        _surface.Bind(this);
    }

    public McpWrapperMode Mode => _options.WrapperMode;

    /// <summary>The surface lazy-mode activations go through.</summary>
    public McpTypedToolSurface Surface => _surface;

    /// <summary>Runs every typed tool, whether it is registered (eager) or activated (lazy).</summary>
    public IToolExecutor Executor => _executor;

    /// <summary>Every registered wrapper.</summary>
    public IReadOnlyCollection<McpWrapperTool> Wrappers => [.. _byName.Values];

    /// <summary>The wrapper registered as <paramref name="name"/>, if any.</summary>
    public bool TryGet(string name, out McpWrapperTool wrapper)
    {
        if (_byName.TryGetValue(name, out var found))
        {
            wrapper = found;
            return true;
        }

        wrapper = null!;
        return false;
    }

    /// <summary>The typed tools of <paramref name="serverName"/>.</summary>
    public IReadOnlyList<McpWrapperTool> WrappersFor(string serverName) =>
        [.. _byName.Values.Where(w => string.Equals(w.ServerName, serverName, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The registration a typed tool has, or would have, in the registry.</summary>
    public static ToolRegistration RegistrationFor(McpWrapperTool wrapper) => new()
    {
        Name = wrapper.Name,
        Description = DescriptionFor(wrapper),
        ParametersSchema = wrapper.InputSchema,
        Source = McpWrapperSource(wrapper.ServerName),
        DownstreamName = wrapper.ToolName
    };

    /// <summary>A typed tool as a function for one session's tool list (lazy mode).</summary>
    public AIFunction CreateFunction(McpWrapperTool wrapper, string? toolSessionId) =>
        new RegistryToolFunction(RegistrationFor(wrapper), _executor, toolSessionId);

    /// <summary>
    /// Reconciles the wrappers of every server the index message names. Call after the
    /// handler has invalidated cached schemas for servers whose surface moved.
    /// </summary>
    public async Task ApplyAsync(McpServersIndexed message, CancellationToken ct)
    {
        if (_options.WrapperMode == McpWrapperMode.Off)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var stale = new HashSet<string>(StringComparer.Ordinal);

            foreach (var removed in message.RemovedServers)
                Reconcile(removed, [], stale);

            foreach (var server in message.Servers)
            {
                var schemas = await _schemas.GetServerAsync(server.ServerName, ct);
                if (schemas is null)
                {
                    // Keep whatever is registered: a slow bridge round trip isn't a reason to
                    // pull working tools out from under the model.
                    _logger.LogWarning(
                        "Could not read tool schemas for MCP server {Server}; its typed tools are unchanged", server.ServerName);
                    continue;
                }

                Reconcile(server.ServerName, Desired(server, schemas), stale);
            }

            _byName = _byServer.Values
                .SelectMany(tools => tools.Values)
                .ToDictionary(w => w.Name, StringComparer.Ordinal);

            // A session that activated a tool that has since gone or changed must search again
            // and get the current schema, so its activation goes.
            _surface.Evict(stale);

            _logger.LogInformation("Typed MCP tools ({Mode}): {Count} across {Servers} server(s)",
                _options.WrapperMode, _byName.Count, _byServer.Count(kvp => kvp.Value.Count > 0));
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, McpWrapperTool> Desired(McpServerSummary server, IReadOnlyList<McpToolDefinition> tools)
    {
        var desired = new Dictionary<string, McpWrapperTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = McpWrapperNaming.For(server.ServerName, tool.Name);

            if (!McpWrapperNaming.FitsProviderLimit(name))
            {
                ReportSkip(name, $"MCP tool {server.ServerName}/{tool.Name} has no typed tool: '{name}' is longer than " +
                                 $"{McpWrapperNaming.MaxLength} characters. It is reachable through mcp_invoke_tool.");
                continue;
            }

            if (desired.TryGetValue(name, out var first))
            {
                ReportSkip(name, $"MCP tools {server.ServerName}/{first.ToolName} and {server.ServerName}/{tool.Name} " +
                                 $"both map to '{name}'; keeping the first. The second is reachable through mcp_invoke_tool.");
                continue;
            }

            var owner = _byName.TryGetValue(name, out var existing) ? existing.ServerName : null;
            if (owner is not null && !string.Equals(owner, server.ServerName, StringComparison.OrdinalIgnoreCase))
            {
                ReportSkip(name, $"MCP tool {server.ServerName}/{tool.Name} has no typed tool: '{name}' already belongs to server '{owner}'.");
                continue;
            }

            if (owner is null && _registry.GetExecutor(name) is not null)
            {
                ReportSkip(name, $"MCP tool {server.ServerName}/{tool.Name} has no typed tool: '{name}' is already a registered tool.");
                continue;
            }

            desired[name] = new McpWrapperTool(
                name,
                server.ServerName,
                server.ServerId,
                tool.Name,
                tool.Description,
                tool.ParametersSchema,
                McpSurfaceFingerprint.Tool(tool.Name, tool.Description, tool.ParametersSchema));
        }

        return desired;
    }

    private void Reconcile(string serverName, Dictionary<string, McpWrapperTool> desired, HashSet<string> stale)
    {
        _byServer.TryGetValue(serverName, out var current);
        current ??= [];
        var register = _options.WrapperMode == McpWrapperMode.Eager;

        foreach (var (name, wrapper) in current)
        {
            if (!desired.TryGetValue(name, out var next) || next.Fingerprint != wrapper.Fingerprint)
            {
                if (register)
                    _registry.Unregister(name);
                stale.Add(name);
            }
            else if (next.ServerId != wrapper.ServerId)
            {
                stale.Add(name);
            }
        }

        // Lazy: indexed here only; sessions add the tools they activate.
        if (!register)
        {
            if (desired.Count == 0)
                _byServer.Remove(serverName);
            else
                _byServer[serverName] = desired;
            return;
        }

        var failed = new List<string>();
        foreach (var (name, wrapper) in desired)
        {
            if (current.TryGetValue(name, out var existing) && existing.Fingerprint == wrapper.Fingerprint)
                continue;

            try
            {
                _registry.Register(RegistrationFor(wrapper), _executor);
            }
            catch (InvalidOperationException ex)
            {
                // Something else registered the name between the collision check and now.
                _logger.LogWarning(ex, "Typed MCP tool '{Name}' could not be registered", name);
                failed.Add(name);
            }
        }

        foreach (var name in failed)
            desired.Remove(name);

        if (desired.Count == 0)
            _byServer.Remove(serverName);
        else
            _byServer[serverName] = desired;
    }

    private static string DescriptionFor(McpWrapperTool wrapper) =>
        string.IsNullOrWhiteSpace(wrapper.Description)
            ? $"[{wrapper.ServerName}] {wrapper.ToolName}"
            : $"[{wrapper.ServerName}] {wrapper.Description}";

    /// <summary>The registry source of a server's typed tools.</summary>
    public static string McpWrapperSource(string serverName) => $"mcp:{serverName}";

    private void ReportSkip(string name, string message)
    {
        lock (_reportedSkips)
        {
            if (!_reportedSkips.Add(name)) return;
        }
        _logger.LogWarning("{Message}", message);
    }
}

/// <summary>
/// Executes a typed wrapper: checks the required parameters, then takes the same path as
/// <c>mcp_invoke_tool</c> (<see cref="McpManagementExecutor.InvokeDownstreamAsync"/>), so guards,
/// attachments, elicitation, timeouts, recovery and error hints all apply unchanged.
/// </summary>
public sealed class McpWrapperToolExecutor : IToolExecutor
{
    private readonly McpWrapperCatalog _catalog;
    private readonly McpManagementExecutor _management;

    internal McpWrapperToolExecutor(McpWrapperCatalog catalog, McpManagementExecutor management)
    {
        _catalog = catalog;
        _management = management;
    }

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        if (!_catalog.TryGet(request.ToolName, out var wrapper))
        {
            return Error(request,
                $"'{request.ToolName}' is no longer available — its MCP server was removed or changed. " +
                "Call mcp_list_services to see what is available now.");
        }

        Dictionary<string, object?> arguments;
        try
        {
            arguments = McpToolExecutor.ParseArguments(request.Arguments);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Error(request, $"Arguments for '{wrapper.Name}' must be a JSON object: {ex.Message}");
        }

        // The one check made before the call: a required key the model left out. Sending the call
        // anyway would only come back as an error that sounds like the tool is broken.
        if (McpCallDiagnostics.DescribeMissingRequired(wrapper.Name, wrapper.InputSchema, arguments) is { } missing)
            return Error(request, missing);

        return await _management.InvokeDownstreamAsync(
            wrapper.ServerName, wrapper.ToolName, request.Arguments, request, McpInvocationPath.Wrapper, ct);
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = message,
        IsError = true
    };
}
