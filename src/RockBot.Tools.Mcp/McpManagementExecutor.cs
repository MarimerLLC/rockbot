using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// <see cref="IToolExecutor"/> for the 8 MCP management tools registered by
/// <see cref="McpServersIndexedHandler"/>:
/// <list type="bullet">
///   <item><c>mcp_list_services</c> — returns cached server index</item>
///   <item><c>mcp_get_service_details</c> — requests tool schemas from bridge</item>
///   <item><c>mcp_invoke_tool</c> — delegates to <see cref="McpToolProxy"/></item>
///   <item><c>mcp_register_server</c> — asks bridge to connect a new server</item>
///   <item><c>mcp_unregister_server</c> — asks bridge to remove a server</item>
///   <item><c>mcp_get_prompt</c> — invokes a prompt template on an MCP server</item>
///   <item><c>mcp_list_resources</c> — lists a server's resources and resource templates</item>
///   <item><c>mcp_read_resource</c> — reads one resource by the server's URI</item>
/// </list>
/// </summary>
public sealed class McpManagementExecutor : IToolExecutor, IAsyncDisposable
{
    private readonly McpServerIndex _index;
    private readonly McpToolProxy _proxy;
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly AgentIdentity _identity;
    private readonly ILogger<McpManagementExecutor> _logger;
    private readonly TimeSpan _timeout;
    private readonly McpRecoveryExecutor? _recovery;
    private readonly ISkillStore? _skillStore;
    private readonly ToolSchemaCache? _schemaCache;
    private readonly McpTypedToolSurface? _typedTools;
    private readonly IMcpSkillSurface? _skillSurface;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pending = new();
    private ISubscription? _responseSubscription;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public const string ManageTopic = "mcp.manage";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpManagementExecutor(
        McpServerIndex index,
        McpToolProxy proxy,
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        AgentIdentity identity,
        ILogger<McpManagementExecutor> logger,
        TimeSpan? timeout = null,
        McpRecoveryExecutor? recovery = null,
        ISkillStore? skillStore = null,
        ToolSchemaCache? schemaCache = null,
        McpTypedToolSurface? typedTools = null,
        IMcpSkillSurface? skillSurface = null)
    {
        _index = index;
        _proxy = proxy;
        _publisher = publisher;
        _subscriber = subscriber;
        _identity = identity;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _recovery = recovery;
        _skillStore = skillStore;
        _schemaCache = schemaCache;
        _typedTools = typedTools;
        _skillSurface = skillSurface;
    }

    public string ResponseTopic => $"mcp.manage.response.{_identity.Name}";

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
        request.ToolName switch
        {
            "mcp_list_services"       => ListServicesAsync(request, ct),
            "mcp_get_service_details" => GetServiceDetailsAsync(request, ct),
            "mcp_invoke_tool"         => InvokeToolAsync(request, ct),
            "mcp_register_server"     => RegisterServerAsync(request, ct),
            "mcp_unregister_server"   => UnregisterServerAsync(request, ct),
            "mcp_get_prompt"          => GetPromptAsync(request, ct),
            "mcp_list_resources"      => ListResourcesAsync(request, ct),
            "mcp_read_resource"       => ReadResourceAsync(request, ct),
            _ => Task.FromResult(Error(request, $"Unknown management tool: {request.ToolName}"))
        };

    // ── mcp_list_services ────────────────────────────────────────────────────

    private async Task<ToolInvokeResponse> ListServicesAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var renamed = await FindRenamedServerSkillsAsync(ct);

        // Fingerprints are bookkeeping for the gateway, not information for the model.
        var view = _index.Snapshot().Select(s => new ServiceView(
            s.ServerName,
            s.ServerId,
            s.Summary,
            s.ToolCount,
            s.ToolNames,
            s.PromptCount,
            s.PromptNames,
            s.ResourceCount > 0 ? s.ResourceCount : null,
            s.ResourceCount > 0 ? s.ResourceNames : null,
            renamed.TryGetValue(s.ServerName, out var skills) ? skills : null));
        var json = JsonSerializer.Serialize(view, JsonOptions);
        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = json
        };
    }

    /// <summary>
    /// <c>mcp_list_services</c> row. <see cref="SkillsWrittenUnderPreviousName"/> lists
    /// <c>mcp/{old}</c> skills whose server now runs under this name (#615), so they don't go
    /// unused without anyone noticing; omitted when there are none. The resource fields (#617)
    /// are likewise omitted for a server without resources.
    /// </summary>
    private sealed record ServiceView(
        string ServerName,
        string? ServerId,
        string? Summary,
        int ToolCount,
        List<string> ToolNames,
        int PromptCount,
        List<string> PromptNames,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? ResourceCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        List<string>? ResourceNames,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<RenamedSkillView>? SkillsWrittenUnderPreviousName);

    private sealed record RenamedSkillView(string Skill, string PreviousServerName);

    /// <summary>
    /// <c>mcp/{server}</c> skills whose server is gone under that name but live under another,
    /// keyed by the current name. Empty without a skill store or skill surface.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<RenamedSkillView>>> FindRenamedServerSkillsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<RenamedSkillView>>(StringComparer.OrdinalIgnoreCase);
        if (_skillStore is null || _skillSurface is null)
            return result;

        IReadOnlyList<Skill> skills;
        try
        {
            skills = await _skillStore.ListAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "mcp_list_services: listing skills for rename detection failed");
            return result;
        }

        foreach (var group in skills
                     .Where(s => s.SurfaceBaseline is not null && McpSkillNames.TryGetServerName(s.Name, out _))
                     .Select(s => (Skill: s, Freshness: _skillSurface.Evaluate(s)))
                     .Where(x => x.Freshness is { Status: SkillFreshnessStatus.Renamed, RenamedTo: not null })
                     .GroupBy(x => x.Freshness.RenamedTo!, StringComparer.OrdinalIgnoreCase))
        {
            result[group.Key] = group
                .OrderBy(x => x.Skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new RenamedSkillView(x.Skill.Name, x.Skill.SurfaceBaseline!.ServerName))
                .ToList();
        }
        return result;
    }

    // ── mcp_get_service_details ──────────────────────────────────────────────

    private async Task<ToolInvokeResponse> GetServiceDetailsAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");

        var details = await GetServiceDetailsAsync(serverName, ct);
        if (details is null)
            return Error(request, $"Timed out waiting for service details for '{serverName}'");
        if (details.Error is not null)
            return Error(request, details.Error);

        var tools = (IReadOnlyList<McpToolDefinition>)details.Tools;
        if (TryGetString(args, "tool_name", out var toolName))
        {
            var allTools = tools;
            tools = tools
                .Where(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tools.Count == 0)
            {
                return Error(request, McpCallDiagnostics.DescribeUnknownTool(
                    serverName, toolName, allTools.Select(t => t.Name).ToList()));
            }
        }

        // Surface the server's self-reported identity alongside tool schemas so the
        // agent can reason about what the server actually is — not just what tools
        // happen to be exposed. The MCP `initialize` handshake provides name/version
        // and a free-text instructions string; both shape how the LLM picks and uses
        // a server. Always include the "server" object even when fields are null so
        // the contract is stable for the agent's tool-result parser.
        var payload = new
        {
            server = new
            {
                name = serverName,
                id = details.ServerId,
                implementationName = details.ImplementationName,
                title = details.Title,
                version = details.Version,
                description = details.Description,
                instructions = McpInstructionsCap.Apply(details.Instructions, McpInstructionsCap.DetailsMaxChars,
                    GuideToolsFor(serverName, details.Tools))
            },
            tools,
            prompts = PromptViews(serverName, details.Prompts)
        };

        var content = JsonSerializer.Serialize(payload, JsonOptions);

        // Resources aren't tools, so they aren't in the payload; only a server that has some
        // says so (#617), keeping every other details result as it was.
        if (details.ResourceCount > 0)
        {
            content += $"\n\nThis server also lists {details.ResourceCount} resource(s) and resource template(s): " +
                       $"readable data rather than tools. List them with mcp_list_resources(server_name: \"{serverName}\") " +
                       "and read one with mcp_read_resource.";
        }

        // Lazy typed tools (#612): looking at a server's tools (or one of them) activates them
        // for this session, so the next call can use the typed name. Only a run that activates
        // typed tools is told so (#613).
        if (_typedTools is not null)
        {
            var activated = _typedTools.Activate(request.SessionId,
                tools.Count == 1 ? _typedTools.WrappersFor(serverName, tools[0].Name) : _typedTools.WrappersFor(serverName));
            if (activated.Count > 0 && TypedToolSurfaceContext.IsActivating)
            {
                content += $"\n\nNow callable in this conversation by typed name: {string.Join(", ", activated)}. " +
                           "Call them directly rather than through mcp_invoke_tool.";
            }
        }

        // Pre-flight skill injection: the LLM has just narrowed in on a single
        // server, so this is the moment any `mcp/{server}` skills become uniquely
        // relevant. Append their content to the tool response so the LLM sees
        // verified parameter shape in the same turn it receives the schema —
        // before the first mcp_invoke_tool attempt. No-op when no skill exists.
        try
        {
            var skillBlock = await McpServerSkillFormatter.FormatAsync(_skillStore, serverName, ct, _skillSurface);
            if (!string.IsNullOrEmpty(skillBlock))
                content = content + "\n" + skillBlock;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to append skill injection block for server '{Server}'", serverName);
        }

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content
        };
    }

    /// <summary>
    /// The server's prompts as details lists them. In a run that activates typed tools, each prompt
    /// with a typed tool carries its <c>typedName</c> (#616): calling that name activates it. Listing
    /// a prompt here never activates it.
    /// </summary>
    private List<PromptView> PromptViews(string serverName, List<McpPromptDefinition> prompts)
    {
        var typed = _typedTools is not null && TypedToolSurfaceContext.IsActivating;
        return prompts
            .Select(p => new PromptView(
                p.Name,
                p.Description,
                p.Arguments,
                typed ? _typedTools!.PromptWrapperFor(serverName, p.Name)?.Name : null))
            .ToList();
    }

    private sealed record PromptView(
        string Name,
        string? Description,
        List<McpPromptArgument> Arguments,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TypedName);

    /// <summary>
    /// The server's guide-like tools (<see cref="McpInstructionsCap.GuideTools"/>), named the way
    /// this run calls them: by typed name when it has typed tools, else through mcp_invoke_tool.
    /// </summary>
    private List<string> GuideToolsFor(string serverName, IEnumerable<McpToolDefinition> tools)
    {
        var typed = TypedToolSurfaceContext.Mode is TypedToolMode.Eager or TypedToolMode.Lazy or TypedToolMode.Pinned;
        return McpInstructionsCap.GuideTools(tools.Select(t => t.Name))
            .Select(name => typed && _typedTools?.WrappersFor(serverName, name).FirstOrDefault() is { } wrapper
                ? $"`{wrapper.Name}`"
                : $"`{name}` (through mcp_invoke_tool)")
            .ToList();
    }

    /// <summary>
    /// Internal entry point used by the recovery layer's <see cref="ToolSchemaCache"/>:
    /// fetches the full tool list for one MCP server via the bridge without going
    /// through the LLM-facing <c>mcp_get_service_details</c> tool. Returns null
    /// on timeout or transport failure.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDefinition>?> GetSchemasAsync(
        string serverName, CancellationToken ct)
    {
        var response = await GetServiceDetailsAsync(serverName, ct);
        if (response is null || response.Error is not null) return null;
        return response.Tools;
    }

    /// <summary>
    /// <see cref="GetSchemasAsync"/> plus the server's prompt definitions, from the same details
    /// round trip (#616). Returns null on timeout or transport failure.
    /// </summary>
    public async Task<McpServerSurface?> GetSurfaceAsync(string serverName, CancellationToken ct)
    {
        var response = await GetServiceDetailsAsync(serverName, ct);
        if (response is null || response.Error is not null) return null;
        return new McpServerSurface(response.Tools, response.Prompts);
    }

    /// <summary>
    /// Fetches the full <see cref="McpGetServiceDetailsResponse"/> for one MCP server,
    /// including server-level identity (name/version/instructions), tool schemas, and prompts.
    /// Returns null on timeout or transport failure.
    /// </summary>
    private async Task<McpGetServiceDetailsResponse?> GetServiceDetailsAsync(
        string serverName, CancellationToken ct)
    {
        var mgmtRequest = new McpGetServiceDetailsRequest { ServerName = serverName };
        var responseEnvelope = await SendRequestAsync(mgmtRequest, ct);
        if (responseEnvelope is null) return null;

        return responseEnvelope.GetPayload<McpGetServiceDetailsResponse>();
    }

    // ── mcp_invoke_tool ──────────────────────────────────────────────────────

    private async Task<ToolInvokeResponse> InvokeToolAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");
        if (!TryGetString(args, "tool_name", out var toolName))
            return Error(request, "Missing required parameter: tool_name");

        // Serialize the nested arguments object if present
        string? toolArgs = null;
        if (TryGetNestedArgs(args, out var argsObj))
        {
            toolArgs = argsObj is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(argsObj, JsonOptions);
        }
        else if (HasNonReservedKeys(args))
        {
            // The call carries inner-tool fields at the top level instead of nested
            // under `arguments`. Some models drop the wrapper and inline the schema
            // fields directly (e.g. `mcp_invoke_tool(server_name=…, tool_name=…,
            // accountId=…, query=…)`). Without this branch the call dispatches with
            // no inner args and the server rejects with "X is required" — recovery's
            // SchemaErrorEnricher then surfaces a missing-field error to the LLM
            // even though the LLM actually supplied the field, just one level up.
            // Promote every non-reserved top-level key into a synthetic arguments
            // object so the call still works.
            var promoted = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in args)
            {
                if (!ReservedTopLevelKeys.Contains(k))
                    promoted[k] = v;
            }
            toolArgs = JsonSerializer.Serialize(promoted, JsonOptions);
            _logger.LogInformation(
                "mcp_invoke_tool: promoted {Count} flat top-level field(s) into 'arguments' for {Server}/{Tool}",
                promoted.Count, serverName, toolName);
        }
        else
        {
            // The call carries only server_name + tool_name — no nested arguments wrapper
            // and no flattened inner-tool fields. Some models hit this when invoking a
            // parameterized MCP tool: they load the schema, then emit an empty call and
            // rationalise the dispatch failure as "the wrapper can't pass arguments,"
            // saving that rationalisation to working memory (see design/self-repair.md).
            // If we can confirm from the cached schema that the target tool requires
            // parameters, short-circuit with a remediation error that names the missing
            // fields and shows the correct wrapper shape. Falls through when the cache
            // is unavailable or the tool genuinely takes no arguments.
            var remediation = await TryBuildEmptyArgsRemediationAsync(serverName, toolName, ct);
            if (remediation is not null)
                return Error(request, remediation);
        }

        return await InvokeDownstreamAsync(serverName, toolName, toolArgs, request, McpInvocationPath.InvokeTool, ct);
    }

    /// <summary>
    /// The one path every downstream MCP tool call takes, whether the model used
    /// <c>mcp_invoke_tool</c> or a typed <c>{server}__{tool}</c> wrapper: the bridge (guards,
    /// attachments, elicitation, timeouts, reconnect-and-retry, error hints) and then recovery.
    /// <paramref name="via"/> tags logs and metrics so reliability can be compared by surface.
    /// </summary>
    internal async Task<ToolInvokeResponse> InvokeDownstreamAsync(
        string serverName,
        string toolName,
        string? toolArgs,
        ToolInvokeRequest outer,
        string via,
        CancellationToken ct)
    {
        var innerRequest = new ToolInvokeRequest
        {
            ToolCallId = outer.ToolCallId,
            ToolName = toolName,
            Arguments = toolArgs,
            SessionId = outer.SessionId
        };

        var extraHeaders = new Dictionary<string, string>
        {
            [McpHeaders.ServerName] = serverName
        };

        var response = await _proxy.ExecuteAsync(innerRequest, extraHeaders, ct);

        // Always pass through recovery — it inspects both IsError=true responses
        // and IsError=false responses with embedded JSON error bodies (some MCP
        // servers report schema errors that way). Recovery short-circuits cheaply
        // when the response is genuinely successful. SessionId from the outer
        // request is forwarded so post-recovery failures cluster by distinct
        // sessions in IFailureClusterStore (Phase 5).
        if (_recovery is not null)
        {
            response = await _recovery.RecoverAsync(
                serverName, toolName, innerRequest, response, ct, sessionId: outer.SessionId);
        }

        // Pinned typed tools (#613): a server this session calls keeps all its typed tools in
        // the session's list. Either path counts; a failed call does too, since the typed tool's
        // schema is the likeliest way to get the next one right.
        _typedTools?.Pin(outer.SessionId, serverName);

        McpDiagnostics.RecordInvocation(serverName, via, response.IsError);
        _logger.LogDebug("MCP {Server}/{Tool} via {Via}: {Outcome}",
            serverName, toolName, via, response.IsError ? "error" : "ok");

        return response;
    }

    // ── mcp_register_server ──────────────────────────────────────────────────

    private async Task<ToolInvokeResponse> RegisterServerAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetString(args, "name", out var name))
            return Error(request, "Missing required parameter: name");
        if (!TryGetString(args, "type", out var type))
            return Error(request, "Missing required parameter: type");
        if (!TryGetString(args, "url", out var url))
            return Error(request, "Missing required parameter: url");

        var mgmtRequest = new McpRegisterServerRequest
        {
            ServerName = name,
            Type = type,
            Url = url
        };

        var responseEnvelope = await SendRequestAsync(mgmtRequest, ct);
        if (responseEnvelope is null)
            return Error(request, "Timed out waiting for server registration response");

        var response = responseEnvelope.GetPayload<McpRegisterServerResponse>();
        if (response is null)
            return Error(request, "Failed to deserialize registration response");

        var content = response.Success
            ? $"Server '{response.ServerName}' registered successfully." +
              (response.Summary is not null ? $" {response.Summary}" : "")
            : $"Failed to register server '{response.ServerName}': {response.Error}";

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = !response.Success
        };
    }

    // ── mcp_unregister_server ────────────────────────────────────────────────

    private async Task<ToolInvokeResponse> UnregisterServerAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");

        var mgmtRequest = new McpUnregisterServerRequest { ServerName = serverName };

        var responseEnvelope = await SendRequestAsync(mgmtRequest, ct);
        if (responseEnvelope is null)
            return Error(request, "Timed out waiting for server unregistration response");

        var response = responseEnvelope.GetPayload<McpUnregisterServerResponse>();
        if (response is null)
            return Error(request, "Failed to deserialize unregistration response");

        var content = response.Success
            ? $"Server '{response.ServerName}' removed successfully."
            : $"Failed to remove server '{response.ServerName}': {response.Error}";

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = !response.Success
        };
    }

    // ── mcp_get_prompt ───────────────────────────────────────────────────────

    private async Task<ToolInvokeResponse> GetPromptAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");
        if (!TryGetString(args, "prompt_name", out var promptName))
            return Error(request, "Missing required parameter: prompt_name");

        var promptArgs = new Dictionary<string, string>();
        if (TryGetNestedArgs(args, out var argsObj))
        {
            var argsJson = argsObj is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(argsObj, JsonOptions);
            promptArgs = ToPromptArguments(argsJson);
        }

        return await GetPromptDownstreamAsync(serverName, promptName, promptArgs, request, McpInvocationPath.GetPrompt, ct);
    }

    /// <summary>
    /// Prompt arguments as the bridge takes them: every value a string. A JSON string is passed
    /// as-is, anything else as its raw JSON text. Malformed JSON yields no arguments.
    /// </summary>
    internal static Dictionary<string, string> ToPromptArguments(string? argsJson)
    {
        var promptArgs = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(argsJson))
            return promptArgs;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson, JsonOptions);
            if (parsed is not null)
            {
                foreach (var (k, v) in parsed)
                {
                    promptArgs[k] = v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? string.Empty
                        : v.GetRawText();
                }
            }
        }
        catch { /* ignore malformed arguments */ }
        return promptArgs;
    }

    /// <summary>
    /// The one path every downstream MCP prompt takes, whether the model used <c>mcp_get_prompt</c>
    /// or a typed <c>{server}__{prompt}-prompt</c> tool (#616): the bridge's <c>prompts/get</c>
    /// (pre-check, timeout, reconnect-and-retry), then the filled-in messages as JSON.
    /// <paramref name="via"/> tags logs and metrics, as <see cref="InvokeDownstreamAsync"/> does.
    /// </summary>
    internal async Task<ToolInvokeResponse> GetPromptDownstreamAsync(
        string serverName,
        string promptName,
        Dictionary<string, string> promptArgs,
        ToolInvokeRequest outer,
        string via,
        CancellationToken ct)
    {
        var mgmtRequest = new McpGetPromptRequest
        {
            ServerName = serverName,
            PromptName = promptName,
            Arguments = promptArgs
        };

        var responseEnvelope = await SendRequestAsync(mgmtRequest, ct);
        var response = responseEnvelope?.GetPayload<McpGetPromptResponse>();
        var result = responseEnvelope is null
            ? Error(outer, $"Timed out waiting for prompt '{promptName}' from server '{serverName}'")
            : response is null
                ? Error(outer, "Failed to deserialize prompt response")
                : response.Error is not null
                    ? Error(outer, response.Error)
                    : new ToolInvokeResponse
                    {
                        ToolCallId = outer.ToolCallId,
                        ToolName = outer.ToolName,
                        Content = JsonSerializer.Serialize(response.Messages, JsonOptions)
                    };

        McpDiagnostics.RecordPromptInvocation(serverName, via, result.IsError);
        _logger.LogDebug("MCP prompt {Server}/{Prompt} via {Via}: {Outcome}",
            serverName, promptName, via, result.IsError ? "error" : "ok");

        return result;
    }

    // ── mcp_list_resources / mcp_read_resource (#617) ────────────────────────

    private async Task<ToolInvokeResponse> ListResourcesAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");

        var responseEnvelope = await SendRequestAsync(new McpListResourcesRequest { ServerName = serverName }, ct);
        if (responseEnvelope is null)
            return Error(request, $"Timed out waiting for the resource list of '{serverName}'");
        var response = responseEnvelope.GetPayload<McpListResourcesResponse>();
        if (response is null)
            return Error(request, "Failed to deserialize resource list response");
        if (response.Error is not null)
            return Error(request, response.Error);

        var content = response.Resources.Count + response.Templates.Count == 0
            ? $"MCP server '{serverName}' lists no resources or resource templates."
            : JsonSerializer.Serialize(new
            {
                server = serverName,
                resources = response.Resources.Select(ResourceView),
                templates = response.Templates.Select(ResourceView)
            }, ResourceJsonOptions);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content
        };

        static object ResourceView(McpResourceDefinition r) => new
        {
            uri = r.IsTemplate ? null : r.Uri,
            uriTemplate = r.IsTemplate ? r.Uri : null,
            name = r.Name,
            title = r.Title,
            description = r.Description,
            mimeType = r.MimeType,
            size = r.Size
        };
    }

    private async Task<ToolInvokeResponse> ReadResourceAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);
        if (!TryGetServerName(args, out var serverName))
            return Error(request, "Missing required parameter: server_name");
        if (!TryGetString(args, "uri", out var uri))
            return Error(request, "Missing required parameter: uri");

        var responseEnvelope = await SendRequestAsync(new McpReadResourceRequest { ServerName = serverName, Uri = uri }, ct);
        var response = responseEnvelope?.GetPayload<McpReadResourceResponse>();
        var result = responseEnvelope is null
            ? Error(request, $"Timed out waiting for resource '{uri}' from server '{serverName}'")
            : response is null
                ? Error(request, "Failed to deserialize resource read response")
                : response.Error is not null
                    ? Error(request, response.Error)
                    : new ToolInvokeResponse
                    {
                        ToolCallId = request.ToolCallId,
                        ToolName = request.ToolName,
                        Content = JsonSerializer.Serialize(
                            new { server = serverName, uri, contents = response.Contents }, ResourceJsonOptions)
                    };

        McpDiagnostics.RecordResourceRead(serverName, result.IsError);
        _logger.LogDebug("MCP resource read {Server} {Uri}: {Outcome}", serverName, uri, result.IsError ? "error" : "ok");
        return result;
    }

    /// <summary>
    /// Resource views leave out absent fields: a text item has no path, a saved file no text, and
    /// most resources have no title or size.
    /// </summary>
    private static readonly JsonSerializerOptions ResourceJsonOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Request-response infrastructure ─────────────────────────────────────

    private async Task<MessageEnvelope?> SendRequestAsync<T>(T payload, CancellationToken ct)
    {
        await EnsureSubscribedAsync(ct);

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            var envelope = payload.ToEnvelope(
                source: _identity.Name,
                correlationId: correlationId,
                replyTo: ResponseTopic);

            await _publisher.PublishAsync(ManageTopic, envelope, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Management request timed out after {TimeoutMs}ms", _timeout.TotalMilliseconds);
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task EnsureSubscribedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _responseSubscription = await _subscriber.SubscribeAsync(
                ResponseTopic,
                $"mcp-management.{_identity.Name}",
                HandleResponseAsync,
                ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task<MessageResult> HandleResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null || !_pending.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            _logger.LogWarning("Received management response with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        tcs.TrySetResult(envelope);
        return Task.FromResult(MessageResult.Ack);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
            if (raw is null) return [];
            return raw.ToDictionary(kvp => kvp.Key, kvp => McpToolExecutor.ConvertJsonElement(kvp.Value));
        }
        catch
        {
            return [];
        }
    }

    private static bool TryGetString(Dictionary<string, object?> args, string key, out string value)
    {
        if (!args.TryGetValue(key, out var raw) || raw is null)
        {
            value = string.Empty;
            return false;
        }

        value = raw switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? string.Empty,
            JsonElement je => je.GetRawText(),
            string s => s,
            _ => raw.ToString() ?? string.Empty
        };
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetServerName(Dictionary<string, object?> args, out string serverName)
    {
        if (!TryGetString(args, "server_name", out serverName))
            return false;
        serverName = serverName.ToLowerInvariant();
        return true;
    }

    // Some models (notably gpt-5.4) emit the nested tool arguments under "params" or "args"
    // instead of the schema-declared "arguments". Accept all three so the payload isn't
    // silently dropped when the model deviates from the declared field name.
    private static readonly string[] NestedArgAliases = ["arguments", "params", "args"];

    private static readonly HashSet<string> ReservedTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_name", "tool_name", "arguments", "params", "args"
    };

    private static bool TryGetNestedArgs(Dictionary<string, object?> args, out object argsObj)
    {
        foreach (var alias in NestedArgAliases)
        {
            if (args.TryGetValue(alias, out var v) && v is not null)
            {
                argsObj = v;
                return true;
            }
        }
        argsObj = null!;
        return false;
    }

    private static bool HasNonReservedKeys(Dictionary<string, object?> args)
    {
        foreach (var key in args.Keys)
        {
            if (!ReservedTopLevelKeys.Contains(key))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a remediation message if the cached schema for the given tool declares
    /// any required parameters. Returns null when the schema cache is unavailable, the
    /// schema can't be fetched, the tool isn't found, or the tool takes no required
    /// parameters — in any of those cases the caller should fall through to the normal
    /// MCP dispatch and let the server respond authoritatively.
    /// </summary>
    private async Task<string?> TryBuildEmptyArgsRemediationAsync(
        string serverName, string toolName, CancellationToken ct)
    {
        if (_schemaCache is null) return null;

        McpToolDefinition? schema;
        try
        {
            schema = await _schemaCache.GetAsync(serverName, toolName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        if (schema is null) return null;

        var required = ExtractRequiredFieldNames(schema.ParametersSchema);
        if (required.Count == 0) return null;

        var sample = BuildExampleArgs(required);
        var requiredList = string.Join(", ", required);
        return
            $"Call to '{serverName}/{toolName}' carried no inner arguments. " +
            $"This tool requires: {requiredList}. " +
            $"Pass the inner-tool parameters nested inside the 'arguments' field of mcp_invoke_tool. " +
            $"Retry with shape: {{\"server_name\":\"{serverName}\",\"tool_name\":\"{toolName}\",\"arguments\":{sample}}}";
    }

    internal static IReadOnlyList<string> ExtractRequiredFieldNames(string? parametersSchema)
    {
        if (string.IsNullOrWhiteSpace(parametersSchema)) return [];
        try
        {
            using var doc = JsonDocument.Parse(parametersSchema);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("required", out var req)) return [];
            if (req.ValueKind != JsonValueKind.Array) return [];
            var names = new List<string>(req.GetArrayLength());
            foreach (var item in req.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string BuildExampleArgs(IReadOnlyList<string> requiredFieldNames)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < requiredFieldNames.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(requiredFieldNames[i]).Append("\":\"…\"");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = message,
        IsError = true
    };

    public async ValueTask DisposeAsync()
    {
        if (_responseSubscription is not null)
            await _responseSubscription.DisposeAsync();

        foreach (var (_, tcs) in _pending)
            tcs.TrySetCanceled();
        _pending.Clear();
        _initLock.Dispose();
    }
}
