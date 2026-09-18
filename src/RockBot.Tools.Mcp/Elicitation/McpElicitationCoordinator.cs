using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Answers one MCP server's <c>elicitation/create</c> requests under that server's configured
/// policy. One instance per connected server; the bridge hands its <see cref="HandleAsync"/> to
/// the MCP client as the elicitation handler, which is also what makes the SDK advertise the
/// elicitation capability during <c>initialize</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule this enforces is that every elicitation terminates, quickly, with one of the three
/// answers the specification defines. A server that asks a question it cannot get answered gets
/// <c>decline</c> and the reason, the agent is told what was asked in the tool result, and the
/// tool call finishes on the server's own terms instead of hanging until the bridge's timeout.
/// </para>
/// <para>
/// Answers are never taken on faith. Configured defaults and responder output alike go through
/// <see cref="McpElicitationSchemaValidator"/>, and any field that looks like a credential
/// declines the whole request before a responder is consulted at all.
/// </para>
/// </remarks>
public sealed class McpElicitationCoordinator
{
    private readonly string _serverName;
    private readonly McpElicitationConfig _config;
    private readonly IMcpElicitationResponder? _responder;
    private readonly ILogger _logger;
    private readonly Dictionary<string, JsonElement> _defaults;
    private readonly ConcurrentDictionary<McpElicitationCallScope, byte> _active = new();

    private McpElicitationCoordinator(
        string serverName,
        McpElicitationConfig config,
        IMcpElicitationResponder? responder,
        ILogger logger)
    {
        _serverName = serverName;
        _config = config;
        _responder = responder;
        _logger = logger;
        _defaults = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Defaults)
            _defaults[pair.Key] = pair.Value;
    }

    /// <summary>
    /// Builds a coordinator for a server, or returns null when the server's policy is
    /// <see cref="McpElicitationConfig.ModeOff"/>.
    /// </summary>
    /// <remarks>
    /// Returning null matters: with no handler the SDK does not advertise the elicitation
    /// capability, so a well-behaved server never asks in the first place. That is a cleaner
    /// answer than advertising support and refusing every question.
    /// </remarks>
    public static McpElicitationCoordinator? TryCreate(
        string serverName,
        McpElicitationConfig? config,
        IMcpElicitationResponder? responder,
        ILogger logger)
    {
        var effective = config ?? new McpElicitationConfig();

        if (!effective.IsRecognizedMode)
        {
            logger.LogWarning(
                "MCP server {Server} has an unrecognized elicitation mode '{Mode}'; treating it as '{Fallback}'",
                serverName, effective.Mode, McpElicitationConfig.ModeDecline);
        }

        var mode = effective.ResolveMode();
        if (mode == McpElicitationConfig.ModeOff)
        {
            logger.LogDebug(
                "MCP server {Server} has elicitation disabled; the capability will not be advertised",
                serverName);
            return null;
        }

        if (mode == McpElicitationConfig.ModeAuto && responder is null)
        {
            logger.LogInformation(
                "MCP server {Server} is configured for automatic elicitation but no responder is " +
                "registered; only configured defaults can be answered",
                serverName);
        }

        return new McpElicitationCoordinator(serverName, effective, responder, logger);
    }

    /// <summary>
    /// Registers a tool call as in flight. Dispose the returned scope when the call finishes;
    /// read <see cref="McpElicitationCallScope.Records"/> first to report what was asked.
    /// </summary>
    public McpElicitationCallScope BeginCall(string toolName, string? arguments)
    {
        var scope = new McpElicitationCallScope(toolName, arguments, s => _active.TryRemove(s, out _));
        _active[scope] = 0;
        return scope;
    }

    /// <summary>
    /// Handles one <c>elicitation/create</c> request. Never throws: an elicitation that fails to
    /// produce an answer is answered <c>decline</c>, because a thrown handler leaves the server
    /// holding an error where the protocol gives it a defined outcome.
    /// </summary>
    public async ValueTask<ElicitResult> HandleAsync(ElicitRequestParams? request, CancellationToken ct)
    {
        try
        {
            return await HandleCoreAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Elicitation handling failed for MCP server {Server}", _serverName);
            return Answer(request, McpElicitationActions.Decline, "the client could not process the request", null);
        }
    }

    private async ValueTask<ElicitResult> HandleCoreAsync(ElicitRequestParams? request, CancellationToken ct)
    {
        if (request is null)
            return Answer(null, McpElicitationActions.Decline, "the request carried no parameters", null);

        var scopes = _active.Keys.ToList();

        // An elicitation with nothing in flight has no caller waiting on it and no tool-call
        // context to answer from — during discovery, say. Decline rather than guess.
        if (scopes.Count == 0)
        {
            return Answer(request, McpElicitationActions.Decline,
                "no tool call was in flight, so there is nothing to answer from", scopes);
        }

        if (_config.ResolveMode() == McpElicitationConfig.ModeDecline)
        {
            return Answer(request, McpElicitationActions.Decline,
                "this client is configured not to answer questions from this server", scopes);
        }

        if (!string.Equals(request.Mode, "form", StringComparison.OrdinalIgnoreCase))
        {
            // url mode expects the client to walk a person through a browser flow. A headless
            // agent has no user agent to hand them, and the SDK only advertises form support,
            // so any url request is a server ignoring our declared capabilities.
            return Answer(request, McpElicitationActions.Decline,
                $"'{request.Mode}' mode elicitation needs a browser and a person; this client only supports form mode",
                scopes);
        }

        var attempts = scopes.Max(s => s.Attempts);
        if (attempts >= _config.MaxPerCall)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"already handled {attempts} question(s) during this tool call (limit {_config.MaxPerCall})",
                scopes);
        }

        foreach (var scope in scopes)
            scope.CountAttempt();

        var sensitive = McpSensitiveFieldDetector.FindSensitiveFields(request.RequestedSchema, _config.DeniedFields);
        if (sensitive.Count > 0)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"the request asks for {string.Join(", ", sensitive)}, which this client never supplies on a user's behalf",
                scopes);
        }

        var defaults = ResolveDefaults(request.RequestedSchema);
        var fieldNames = McpElicitationSchemaDescriber.FieldNames(request.RequestedSchema);

        // Operator-configured defaults that cover the whole form settle it without a model call.
        if (fieldNames.Count > 0 && defaults.Count == fieldNames.Count)
        {
            var configured = McpElicitationSchemaValidator.Validate(request.RequestedSchema, defaults);
            if (configured.IsValid)
                return Answer(request, McpElicitationActions.Accept, "answered from configured defaults", scopes, configured.Content);
        }

        // A form that is nothing but yes/no boxes is a confirmation prompt — a second checkpoint
        // the server put there for a person. Answering it from the model would defeat the point
        // of asking. The fully-defaulted case already returned above, so this is the operator
        // having declined to pre-answer it.
        if (IsConfirmationOnly(request.RequestedSchema))
        {
            return Answer(request, McpElicitationActions.Decline,
                "this is a yes/no decision, which this client does not make on a user's behalf; " +
                "confirm it in the tool arguments or configure an elicitation default",
                scopes);
        }

        if (_responder is null)
        {
            return Answer(request, McpElicitationActions.Decline,
                "this client has no way to answer the question; supply the value in the tool call instead",
                scopes);
        }

        var context = new McpElicitationContext(
            _serverName,
            request,
            [.. scopes.Select(s => new McpElicitationCallContext(s.ToolName, s.Arguments))],
            defaults);

        McpElicitationAnswer answer;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_config.ResponderTimeoutMs > 0)
                budget.CancelAfter(_config.ResponderTimeoutMs);

            answer = await _responder.AnswerAsync(context, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"the client took longer than {_config.ResponderTimeoutMs}ms to work out an answer",
                scopes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elicitation responder failed for MCP server {Server}", _serverName);
            return Answer(request, McpElicitationActions.Decline, "the client failed to work out an answer", scopes);
        }

        if (!answer.Accepted)
        {
            return Answer(request, McpElicitationActions.Decline,
                answer.Reason ?? "the client had no answer for this question", scopes);
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (answer.Content is { Count: > 0 })
        {
            foreach (var pair in answer.Content)
                merged[pair.Key] = pair.Value;
        }

        // Configured defaults overwrite the responder: they are the operator's answer, not a guess.
        foreach (var pair in defaults)
            merged[pair.Key] = pair.Value;

        var validated = McpElicitationSchemaValidator.Validate(request.RequestedSchema, merged);
        if (validated.IgnoredFields.Count > 0)
        {
            _logger.LogDebug(
                "Dropping {Count} field(s) the elicitation answer invented for MCP server {Server}: {Fields}",
                validated.IgnoredFields.Count, _serverName, string.Join(", ", validated.IgnoredFields));
        }

        if (!validated.IsValid)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"the answer did not fit the requested form ({string.Join("; ", validated.Errors)})",
                scopes);
        }

        return Answer(request, McpElicitationActions.Accept, null, scopes, validated.Content);
    }

    /// <summary>
    /// Whether every field the server asked for is a plain boolean — the shape of a pure
    /// confirmation prompt.
    /// </summary>
    private static bool IsConfirmationOnly(ElicitRequestParams.RequestSchema? schema)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
            return false;

        foreach (var pair in properties)
        {
            if (pair.Value is not ElicitRequestParams.BooleanSchema)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Picks the configured defaults that this request actually asks for, dropping any that no
    /// longer fit the server's schema so a stale default is never forwarded.
    /// </summary>
    private Dictionary<string, JsonElement> ResolveDefaults(ElicitRequestParams.RequestSchema? schema)
    {
        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (_defaults.Count == 0 || schema?.Properties is not { Count: > 0 } properties)
            return resolved;

        foreach (var pair in properties)
        {
            if (!_defaults.TryGetValue(pair.Key, out var configured))
                continue;

            var single = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [pair.Key] = configured };
            var check = McpElicitationSchemaValidator.Validate(
                new ElicitRequestParams.RequestSchema { Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { [pair.Key] = pair.Value } },
                single);

            if (check.IsValid)
            {
                resolved[pair.Key] = configured.Clone();
            }
            else
            {
                _logger.LogWarning(
                    "Configured elicitation default for {Server}.{Field} does not fit the schema the server sent: {Errors}",
                    _serverName, pair.Key, string.Join("; ", check.Errors));
            }
        }

        return resolved;
    }

    private ElicitResult Answer(
        ElicitRequestParams? request,
        string action,
        string? reason,
        IReadOnlyList<McpElicitationCallScope>? scopes,
        IReadOnlyDictionary<string, JsonElement>? content = null)
    {
        var answeredFields = content is { Count: > 0 }
            ? content.Keys.ToArray()
            : Array.Empty<string>();

        var record = new McpElicitationRecord
        {
            ServerName = _serverName,
            RequestMode = request?.Mode ?? "form",
            Message = request?.Message ?? "(no message)",
            Action = action,
            Reason = reason,
            RequestedFields = McpElicitationSchemaDescriber.FieldNames(request?.RequestedSchema),
            AnsweredFields = answeredFields,
        };

        if (scopes is { Count: > 0 })
        {
            foreach (var scope in scopes)
                scope.Record(record);
        }

        if (record.IsAccepted)
        {
            _logger.LogInformation(
                "MCP {Server} asked mid-call and was answered: fields=[{Fields}] ({Reason})",
                _serverName, string.Join(", ", record.AnsweredFields), reason ?? "answered");
        }
        else
        {
            _logger.LogInformation(
                "MCP {Server} asked mid-call and was declined: {Reason} | question: {Message}",
                _serverName, reason ?? "(no reason)", McpElicitationSchemaDescriber.Flatten(record.Message));
        }

        return new ElicitResult
        {
            Action = action,
            Content = content is { Count: > 0 }
                ? content.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : null,
        };
    }
}
