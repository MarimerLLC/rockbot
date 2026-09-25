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
    private readonly List<string> _deniedFields;
    private readonly ConcurrentDictionary<McpElicitationCallScope, byte> _active = new();

    /// <summary>
    /// Serializes the per-call cap check with the attempt increment. The SDK dispatches
    /// server-initiated requests concurrently, so a check-then-act would let two simultaneous
    /// questions both slip in under the last remaining round.
    /// </summary>
    private readonly object _capGate = new();

    /// <summary>
    /// Option values that make a single-select field a decision rather than data: "Delete 12
    /// rows?" offered as <c>["yes","no"]</c> is the same checkpoint as a boolean.
    /// </summary>
    private static readonly HashSet<string> DecisionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "no", "y", "n", "true", "false", "ok", "okay",
        "confirm", "confirmed", "cancel", "cancelled", "canceled",
        "approve", "approved", "deny", "denied", "reject", "rejected",
        "accept", "accepted", "decline", "declined", "allow", "disallow",
        "proceed", "continue", "abort", "stop", "skip",
    };

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

        // mcp.json can say "defaults": null or "deniedFields": null. Treat that as empty rather
        // than failing construction, which would surface as a misleading connection failure.
        _defaults = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Defaults ?? [])
            _defaults[pair.Key] = pair.Value;
        _deniedFields = config.DeniedFields ?? [];
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
    public McpElicitationCallScope BeginCall(string toolName, string? arguments, string? sessionId = null)
    {
        var scope = new McpElicitationCallScope(toolName, arguments, sessionId, s => _active.TryRemove(s, out _));
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The SDK withdrew the request (the tool call was cancelled or the session closed).
            // Expected, not a fault — and the agent should still hear that a question was asked.
            _logger.LogDebug("Elicitation for MCP server {Server} was cancelled before it was answered", _serverName);
            return Answer(request, McpElicitationActions.Cancel,
                "the request was cancelled before an answer was ready", [.. _active.Keys]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Elicitation handling failed for MCP server {Server}", _serverName);
            return Answer(request, McpElicitationActions.Decline, "the client could not process the request", [.. _active.Keys]);
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

        int attempts;
        bool overCap;
        lock (_capGate)
        {
            attempts = scopes.Max(s => s.Attempts);
            overCap = attempts >= _config.MaxPerCall;
            if (!overCap)
            {
                foreach (var scope in scopes)
                    scope.CountAttempt();
            }
        }

        if (overCap)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"already handled {attempts} question(s) during this tool call (limit {_config.MaxPerCall})",
                scopes);
        }

        // A field the SDK could not model (a type outside MCP's primitive subset, e.g. "object")
        // arrives as a null definition with its title and description discarded. The client
        // cannot tell what it is for — including whether it is a credential — so it answers none
        // of the form rather than guess.
        var unreadable = UnreadableFields(request.RequestedSchema);
        if (unreadable.Count > 0)
        {
            return Answer(request, McpElicitationActions.Decline,
                $"the request asks for {string.Join(", ", unreadable)} in a form this client cannot read, " +
                "so it cannot tell what the value is for",
                scopes);
        }

        var sensitive = McpSensitiveFieldDetector.FindSensitiveFields(request.RequestedSchema, _deniedFields);
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

        // A yes/no field — a boolean, or a choice between "yes"/"no"-style options — is a
        // checkpoint the server put there for a person. Answering it from a model would defeat
        // the point of asking, so only an operator default may settle one. A required decision
        // left open declines the form; an optional one is withheld, so the server applies its
        // own default for it.
        var decisions = DecisionFields(request.RequestedSchema, defaults);
        if (decisions.Count > 0
            && (decisions.Count == fieldNames.Count - defaults.Count
                || decisions.Any(f => request.RequestedSchema!.Required?.Contains(f) == true)))
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
            [.. scopes.Select(s => new McpElicitationCallContext(s.ToolName, s.Arguments, s.SessionId))],
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
        catch (OperationCanceledException)
        {
            // The SDK withdrew the request; HandleAsync answers it as a cancellation.
            throw;
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

        // Match the responder's keys to the server's field names case-insensitively, as defaults
        // already are — "Mailbox" for "mailbox" is the same answer, not an invented field.
        // Decision fields the responder was not allowed to settle are withheld.
        var canonical = fieldNames.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (answer.Content is { Count: > 0 })
        {
            foreach (var pair in answer.Content)
            {
                var key = canonical.GetValueOrDefault(pair.Key, pair.Key);
                if (!decisions.Contains(key, StringComparer.Ordinal))
                    merged[key] = pair.Value;
            }
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

        // "accept" with nothing in it tells the server the user agreed and chose to give no
        // values — a claim nobody made. A form that asked for something and got none of it
        // is a decline.
        if (fieldNames.Count > 0 && validated.Content.Count == 0)
        {
            return Answer(request, McpElicitationActions.Decline,
                "the client's answer supplied none of the requested fields", scopes);
        }

        return Answer(request, McpElicitationActions.Accept, null, scopes, validated.Content);
    }

    /// <summary>
    /// Whether a field asks for a decision rather than data: a boolean, or a single choice
    /// between options that are all yes/no/confirm/cancel-style words.
    /// </summary>
    internal static bool IsDecisionField(ElicitRequestParams.PrimitiveSchemaDefinition? definition)
    {
        return definition switch
        {
            ElicitRequestParams.BooleanSchema => true,
            ElicitRequestParams.UntitledSingleSelectEnumSchema e => AreDecisionWords(e.Enum),
            ElicitRequestParams.TitledSingleSelectEnumSchema e => AreDecisionWords([.. e.OneOf.Select(o => o.Const)]),
#pragma warning disable MCP9001 // deprecated by the spec, still emitted by older servers
            ElicitRequestParams.LegacyTitledEnumSchema e => AreDecisionWords(e.Enum),
#pragma warning restore MCP9001
            _ => false,
        };

        static bool AreDecisionWords(IList<string>? values)
            => values is { Count: > 0 } && values.All(v => DecisionWords.Contains(v?.Trim() ?? string.Empty));
    }

    /// <summary>Decision fields that configured defaults did not settle.</summary>
    private static List<string> DecisionFields(
        ElicitRequestParams.RequestSchema? schema,
        IReadOnlyDictionary<string, JsonElement> defaults)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
            return [];

        return [.. properties
            .Where(p => IsDecisionField(p.Value) && !defaults.ContainsKey(p.Key))
            .Select(p => p.Key)];
    }

    /// <summary>
    /// Fields whose definition the SDK could not deserialize — it yields null for a type outside
    /// the primitive subset MCP allows.
    /// </summary>
    private static List<string> UnreadableFields(ElicitRequestParams.RequestSchema? schema)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
            return [];

        return [.. properties.Where(p => p.Value is null).Select(p => p.Key)];
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

            var check = CheckDefault(pair.Key, pair.Value, configured);

            // Defaults bound from appsettings, Helm or environment variables arrive as strings
            // ("true", "5"). For a boolean or number field, read the operator's string as the
            // JSON literal it spells. This applies to operator configuration only — responder
            // output is never coerced.
            if (!check.IsValid
                && configured.ValueKind == JsonValueKind.String
                && pair.Value is ElicitRequestParams.BooleanSchema or ElicitRequestParams.NumberSchema
                && TryParseLiteral(configured.GetString(), out var literal))
            {
                var literalCheck = CheckDefault(pair.Key, pair.Value, literal);
                if (literalCheck.IsValid)
                {
                    configured = literal;
                    check = literalCheck;
                }
            }

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

    private static McpElicitationValidationResult CheckDefault(
        string name, ElicitRequestParams.PrimitiveSchemaDefinition definition, JsonElement value)
        => McpElicitationSchemaValidator.Validate(
            new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> { [name] = definition },
            },
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [name] = value });

    private static bool TryParseLiteral(string? text, out JsonElement literal)
    {
        literal = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var document = JsonDocument.Parse(text.Trim());
            literal = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
