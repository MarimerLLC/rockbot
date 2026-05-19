using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// <see cref="IToolExecutor"/> for the 6 MCP management tools registered by
/// <see cref="McpServersIndexedHandler"/>:
/// <list type="bullet">
///   <item><c>mcp_list_services</c> — returns cached server index</item>
///   <item><c>mcp_get_service_details</c> — requests tool schemas from bridge</item>
///   <item><c>mcp_invoke_tool</c> — delegates to <see cref="McpToolProxy"/></item>
///   <item><c>mcp_register_server</c> — asks bridge to connect a new server</item>
///   <item><c>mcp_unregister_server</c> — asks bridge to remove a server</item>
///   <item><c>mcp_get_prompt</c> — invokes a prompt template on an MCP server</item>
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
        ISkillStore? skillStore = null)
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
    }

    public string ResponseTopic => $"mcp.manage.response.{_identity.Name}";

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
        request.ToolName switch
        {
            "mcp_list_services"       => Task.FromResult(ListServices(request)),
            "mcp_get_service_details" => GetServiceDetailsAsync(request, ct),
            "mcp_invoke_tool"         => InvokeToolAsync(request, ct),
            "mcp_register_server"     => RegisterServerAsync(request, ct),
            "mcp_unregister_server"   => UnregisterServerAsync(request, ct),
            "mcp_get_prompt"          => GetPromptAsync(request, ct),
            _ => Task.FromResult(Error(request, $"Unknown management tool: {request.ToolName}"))
        };

    // ── mcp_list_services ────────────────────────────────────────────────────

    private ToolInvokeResponse ListServices(ToolInvokeRequest request)
    {
        var json = JsonSerializer.Serialize(_index.Servers, JsonOptions);
        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = json
        };
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
            tools = tools
                .Where(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tools.Count == 0)
                return Error(request, $"No tool named '{toolName}' found on server '{serverName}'");
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
                implementationName = details.ImplementationName,
                title = details.Title,
                version = details.Version,
                description = details.Description,
                instructions = details.Instructions
            },
            tools,
            prompts = details.Prompts
        };

        var content = JsonSerializer.Serialize(payload, JsonOptions);

        // Pre-flight skill injection: the LLM has just narrowed in on a single
        // server, so this is the moment any `mcp/{server}` skills become uniquely
        // relevant. Append their content to the tool response so the LLM sees
        // verified parameter shape in the same turn it receives the schema —
        // before the first mcp_invoke_tool attempt. No-op when no skill exists.
        try
        {
            var skillBlock = await McpServerSkillFormatter.FormatAsync(_skillStore, serverName, ct);
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

        var innerRequest = new ToolInvokeRequest
        {
            ToolCallId = request.ToolCallId,
            ToolName = toolName,
            Arguments = toolArgs
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
                serverName, toolName, innerRequest, response, ct, sessionId: request.SessionId);
        }

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
        }

        var mgmtRequest = new McpGetPromptRequest
        {
            ServerName = serverName,
            PromptName = promptName,
            Arguments = promptArgs
        };

        var responseEnvelope = await SendRequestAsync(mgmtRequest, ct);
        if (responseEnvelope is null)
            return Error(request, $"Timed out waiting for prompt '{promptName}' from server '{serverName}'");

        var response = responseEnvelope.GetPayload<McpGetPromptResponse>();
        if (response is null)
            return Error(request, "Failed to deserialize prompt response");
        if (response.Error is not null)
            return Error(request, response.Error);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = JsonSerializer.Serialize(response.Messages, JsonOptions)
        };
    }

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
