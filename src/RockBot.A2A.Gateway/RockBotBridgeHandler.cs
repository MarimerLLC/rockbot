using System.Security.Claims;
using System.Text.Json;
using A2A;
using Microsoft.Extensions.Options;

using A2ATaskStatus = A2A.TaskStatus;
using RockBot.Messaging;

using RbAgentTaskRequest = RockBot.A2A.AgentTaskRequest;
using RbAgentTaskResult = RockBot.A2A.AgentTaskResult;
using RbAgentTaskCancelRequest = RockBot.A2A.AgentTaskCancelRequest;
using RbAgentMessage = RockBot.A2A.AgentMessage;
using RbAgentMessagePart = RockBot.A2A.AgentMessagePart;
using RbAgentTaskStatusUpdate = RockBot.A2A.AgentTaskStatusUpdate;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Bridges A2A v1 server requests to RockBot's RabbitMQ message handler.
/// Extracts the authenticated caller's identity from <see cref="IHttpContextAccessor"/>
/// and propagates it as the <c>Source</c> on the RabbitMQ envelope so the trust model
/// sees the real caller, not the gateway.
/// </summary>
internal sealed class RockBotBridgeHandler(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    IHttpContextAccessor httpContextAccessor,
    IOptions<GatewayOptions> gatewayOptions,
    ITaskStore taskStore,
    PushNotificationSender pushSender,
    ILogger<RockBotBridgeHandler> logger) : IAgentHandler
{
    private string GetCallerId() =>
        httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "anonymous";

    private IReadOnlyDictionary<string, string>? BuildAuthClaimsHeader() =>
        BuildAuthClaimsHeader(httpContextAccessor.HttpContext?.User);

    /// <summary>
    /// Builds the envelope headers carrying gateway-verified caller claims for token-based
    /// (Bearer/JWT) auth. Returns <c>null</c> for API-key callers — those remain name-based
    /// (self-asserted) on the agent side. The claims are JSON-encoded under
    /// <see cref="WellKnownHeaders.AuthClaims"/> so the agent's identity verifier can mark
    /// the identity as not self-asserted and record the IdP issuer.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? BuildAuthClaimsHeader(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // The API-key handler stamps a literal issuer=api-key claim; those callers are
        // verified only by name, so don't forward a "verified claims" header for them.
        if (string.Equals(user.FindFirst("issuer")?.Value, "api-key", StringComparison.Ordinal))
            return null;

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                claims[key] = value;
        }

        Add("sub", user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value);
        Add("name", user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("name")?.Value);
        // JwtBearer surfaces the token issuer on each claim's Issuer property; fall back to an
        // explicit "iss" claim when present.
        Add("iss", user.FindFirst("iss")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Issuer);
        Add("scope", user.FindFirst("scope")?.Value ?? user.FindFirst("scp")?.Value);

        if (claims.Count == 0)
            return null;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WellKnownHeaders.AuthClaims] = JsonSerializer.Serialize(claims)
        };
    }

    private static string ReplyTopicFor(string callerId) =>
        $"agent.response.gateway.{callerId}";

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var callerId = GetCallerId();
        var replyTopic = ReplyTopicFor(callerId);
        var taskId = context.TaskId ?? Guid.NewGuid().ToString("N");
        var skill = "general";
        if (context.Metadata?.TryGetValue("skill", out var skillEl) == true)
            skill = skillEl.GetString() ?? "general";

        var messageText = context.UserText ?? "(empty)";
        var timeout = TimeSpan.FromSeconds(gatewayOptions.Value.TaskTimeoutSeconds);

        // Request-level metadata — everything except the "skill" key already consumed for routing.
        var requestMetadata = ExtractRequestMetadata(context.Metadata);

        // Message-level metadata — propagate straight through.
        var messageMetadata = StringifyMetadata(context.Message.Metadata);

        // Map all inbound parts — not just the first text — so data parts round-trip too.
        var inboundParts = MapInboundParts(context.Message.Parts, messageText);

        logger.LogInformation(
            "Bridging A2A task {TaskId} skill={Skill} caller={CallerId} streaming={Streaming} to RockBot via RabbitMQ",
            taskId, skill, callerId, context.StreamingResponse);

        // Subscribe for the response BEFORE publishing
        var resultTcs = new TaskCompletionSource<RbAgentTaskResult>();
        var subName = $"a2a-gw-{Guid.NewGuid():N}";
        await using var replySub = await subscriber.SubscribeAsync(
            replyTopic,
            subName,
            (envelope, _) =>
            {
                try
                {
                    var result = envelope.GetPayload<RbAgentTaskResult>();
                    if (result?.TaskId == taskId)
                        resultTcs.TrySetResult(result);
                }
                catch { /* ignore deserialization errors from unrelated messages */ }
                return Task.FromResult(MessageResult.Ack);
            },
            cancellationToken);

        // Subscribe to status updates for intermediate streaming events
        IAsyncDisposable? statusSub = null;
        if (context.StreamingResponse)
        {
            var statusSubName = $"a2a-gw-status-{Guid.NewGuid():N}";
            statusSub = await subscriber.SubscribeAsync(
                "agent.task.status",
                statusSubName,
                async (envelope, _) =>
                {
                    try
                    {
                        var update = envelope.GetPayload<RbAgentTaskStatusUpdate>();
                        if (update is not null && envelope.CorrelationId == taskId)
                        {
                            var statusText = update.Message?.Parts
                                .Where(p => p.Kind == "text")
                                .Select(p => p.Text)
                                .FirstOrDefault();

                            var statusEvent = new TaskStatusUpdateEvent
                            {
                                TaskId = taskId,
                                ContextId = context.ContextId,
                                Status = new A2ATaskStatus
                                {
                                    State = MapTaskState(update.State),
                                    Message = statusText is not null
                                        ? new Message { Role = Role.Agent, Parts = [new Part { Text = statusText }] }
                                        : null,
                                    Timestamp = DateTimeOffset.UtcNow
                                }
                            };
                            await eventQueue.EnqueueStatusUpdateAsync(statusEvent, cancellationToken);
                            // Fire-and-forget push notification
                            var __ = pushSender.TrySendStatusUpdateAsync(taskId, statusEvent, cancellationToken);
                        }
                    }
                    catch { /* ignore deserialization errors */ }
                    return MessageResult.Ack;
                },
                cancellationToken);
        }

        try
        {
            // Brief delay for subscriptions to bind
            await Task.Delay(300, cancellationToken);

            // Publish task to RockBot — propagate contextId for multi-turn continuation
            var request = new RbAgentTaskRequest
            {
                TaskId = taskId,
                ContextId = context.ContextId,
                Skill = skill,
                Metadata = requestMetadata,
                Message = new RbAgentMessage
                {
                    Role = "user",
                    Parts = inboundParts,
                    Metadata = messageMetadata
                }
            };

            var envelope = request.ToEnvelope<RbAgentTaskRequest>(
                source: callerId,
                correlationId: taskId,
                replyTo: replyTopic,
                headers: BuildAuthClaimsHeader());

            await publisher.PublishAsync($"agent.task.{gatewayOptions.Value.RoutingName}", envelope, cancellationToken);

            // Wait for RockBot's response
            var result = await resultTcs.Task.WaitAsync(timeout, cancellationToken);

            logger.LogInformation("Got response for task {TaskId}: state={State}", taskId, result.State);

            // Map RockBot result back to A2A v1 — carry all parts (text + data) and metadata through.
            var responseParts = MapOutboundParts(result.Message?.Parts);
            var responseMessage = new Message
            {
                Role = Role.Agent,
                Parts = responseParts
            };
            var outboundMetadata = ToJsonElementMetadata(result.Message?.Metadata);
            if (outboundMetadata is not null)
                responseMessage.Metadata = outboundMetadata;

            var a2aState = MapTaskState(result.State);
            // Use the contextId from the agent's response (it may have created one
            // for multi-turn tracking), falling back to the caller's contextId.
            var effectiveContextId = result.ContextId ?? context.ContextId;
            var task = new AgentTask
            {
                Id = taskId,
                ContextId = effectiveContextId,
                Status = new A2ATaskStatus
                {
                    State = a2aState,
                    Message = responseMessage,
                    Timestamp = DateTimeOffset.UtcNow
                },
                History = [
                    BuildUserHistoryMessage(context.Message, messageText, messageMetadata),
                    responseMessage
                ]
            };
            await taskStore.SaveTaskAsync(taskId, task, cancellationToken);

            // For terminal states, return a Message response (simple, backwards-compatible).
            // For non-terminal states (InputRequired, Working), return a Task response so the
            // caller's SDK preserves the state and can act on it (e.g. InputRequired follow-up).
            if (a2aState is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
            {
                _ = pushSender.TrySendTaskCompletedAsync(taskId, task, cancellationToken);
                await eventQueue.EnqueueMessageAsync(responseMessage, cancellationToken);
            }
            else
            {
                await eventQueue.EnqueueTaskAsync(task, cancellationToken);
            }
        }
        finally
        {
            if (statusSub is not null)
                await statusSub.DisposeAsync();
        }
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var callerId = GetCallerId();
        var taskId = context.TaskId;

        logger.LogInformation("Cancel requested for task {TaskId} by {CallerId}", taskId, callerId);

        if (taskId is null)
            return;

        var cancelRequest = new RbAgentTaskCancelRequest { TaskId = taskId };
        var envelope = cancelRequest.ToEnvelope<RbAgentTaskCancelRequest>(
            source: callerId,
            correlationId: taskId,
            headers: BuildAuthClaimsHeader());

        await publisher.PublishAsync($"agent.task.cancel.{gatewayOptions.Value.RoutingName}", envelope, cancellationToken);
    }

    internal static IReadOnlyDictionary<string, string>? ExtractRequestMetadata(
        IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var kvp in metadata)
        {
            // "skill" is already consumed for routing; don't double-propagate it.
            if (string.Equals(kvp.Key, "skill", StringComparison.Ordinal))
                continue;
            result[kvp.Key] = JsonElementToString(kvp.Value);
        }
        return result.Count == 0 ? null : result;
    }

    internal static IReadOnlyDictionary<string, string>? StringifyMetadata(
        IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var kvp in metadata)
            result[kvp.Key] = JsonElementToString(kvp.Value);
        return result;
    }

    private static string JsonElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };

    internal static Dictionary<string, JsonElement>? ToJsonElementMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, JsonElement>(metadata.Count, StringComparer.Ordinal);
        foreach (var kvp in metadata)
            result[kvp.Key] = JsonSerializer.SerializeToElement(kvp.Value);
        return result;
    }

    internal static IReadOnlyList<RbAgentMessagePart> MapInboundParts(
        IReadOnlyList<Part>? parts, string fallbackText)
    {
        if (parts is null || parts.Count == 0)
            return [new RbAgentMessagePart { Kind = "text", Text = fallbackText }];

        var mapped = new List<RbAgentMessagePart>(parts.Count);
        foreach (var part in parts)
        {
            switch (part.ContentCase)
            {
                case PartContentCase.Text:
                    mapped.Add(new RbAgentMessagePart { Kind = "text", Text = part.Text });
                    break;
                case PartContentCase.Data:
                    mapped.Add(new RbAgentMessagePart
                    {
                        Kind = "data",
                        Data = part.Data?.GetRawText(),
                        MimeType = part.MediaType
                    });
                    break;
                // Raw/Url parts fall outside the current RbAgentMessagePart model; skip them
                // so handlers see only what the RockBot contract can represent.
            }
        }

        if (mapped.Count == 0)
            mapped.Add(new RbAgentMessagePart { Kind = "text", Text = fallbackText });

        return mapped;
    }

    internal static List<Part> MapOutboundParts(IReadOnlyList<RbAgentMessagePart>? parts)
    {
        if (parts is null || parts.Count == 0)
            return [new Part { Text = "(no response)" }];

        var mapped = new List<Part>(parts.Count);
        foreach (var part in parts)
        {
            if (string.Equals(part.Kind, "data", StringComparison.Ordinal) && part.Data is not null)
            {
                var element = JsonSerializer.Deserialize<JsonElement>(part.Data);
                var a2aPart = Part.FromData(element);
                if (!string.IsNullOrEmpty(part.MimeType))
                    a2aPart.MediaType = part.MimeType;
                mapped.Add(a2aPart);
            }
            else
            {
                mapped.Add(new Part { Text = part.Text ?? string.Empty });
            }
        }
        return mapped;
    }

    private static Message BuildUserHistoryMessage(
        Message? originalMessage,
        string messageText,
        IReadOnlyDictionary<string, string>? messageMetadata)
    {
        // Preserve the client's original parts and metadata verbatim when available
        // so the task history reflects what was actually sent.
        if (originalMessage is not null && originalMessage.Parts.Count > 0)
            return originalMessage;

        var history = new Message
        {
            Role = Role.User,
            Parts = [new Part { Text = messageText }]
        };
        var meta = ToJsonElementMetadata(messageMetadata);
        if (meta is not null)
            history.Metadata = meta;
        return history;
    }

    private static TaskState MapTaskState(AgentTaskState state) => state switch
    {
        AgentTaskState.Submitted => TaskState.Submitted,
        AgentTaskState.Working => TaskState.Working,
        AgentTaskState.InputRequired => TaskState.InputRequired,
        AgentTaskState.Completed => TaskState.Completed,
        AgentTaskState.Failed => TaskState.Failed,
        AgentTaskState.Canceled => TaskState.Canceled,
        _ => TaskState.Working
    };
}
