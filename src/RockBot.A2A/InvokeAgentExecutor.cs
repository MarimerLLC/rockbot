using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using A2AProtocol = A2A.V0_3;

namespace RockBot.A2A;

/// <summary>
/// Publishes an <see cref="AgentTaskRequest"/> to a target agent and registers the
/// pending task in <see cref="A2ATaskTracker"/>. Returns the task ID immediately.
/// Supports both queue-based (RabbitMQ) and HTTP-based transport. HTTP transport is
/// used when the target agent's <see cref="AgentCard"/> has a non-empty <c>Url</c>.
/// </summary>
internal sealed class InvokeAgentExecutor(
    IMessagePublisher publisher,
    A2ATaskTracker tracker,
    IAgentDirectory directory,
    A2AOptions options,
    AgentIdentity identity,
    IHttpClientFactory httpClientFactory,
    ILogger<InvokeAgentExecutor> logger) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON.");
        }

        if (!args.TryGetValue("agent_name", out var agentEl) || agentEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: agent_name");

        if (!args.TryGetValue("skill", out var skillEl) || skillEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: skill");

        if (!args.TryGetValue("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: message");

        var agentName = agentEl.GetString()!;
        var skill = skillEl.GetString()!;
        var messageText = messageEl.GetString()!;
        int timeoutMinutes = args.TryGetValue("timeout_minutes", out var toEl) && toEl.TryGetInt32(out var to) ? to : 5;

        var taskId = Guid.NewGuid().ToString("N");
        var primarySessionId = request.SessionId ?? "unknown";

        var taskRequest = new AgentTaskRequest
        {
            TaskId = taskId,
            Skill = skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = messageText }]
            }
        };

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        var pending = new PendingA2ATask
        {
            TaskId = taskId,
            TargetAgent = agentName,
            Skill = skill,
            PrimarySessionId = primarySessionId,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = cts
        };
        tracker.Track(pending);

        // Prefer HTTP transport when the agent has a URL registered.
        var agentCard = directory.GetAgent(agentName);
        var protocol = !string.IsNullOrEmpty(agentCard?.Url) ? "http" : "queue";

        using var a2aActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.invoke");
        a2aActivity?.SetTag("rockbot.a2a.target_agent", agentName);
        a2aActivity?.SetTag("rockbot.a2a.skill", skill);
        a2aActivity?.SetTag("rockbot.a2a.task_id", taskId);
        a2aActivity?.SetTag("rockbot.a2a.protocol", protocol);

        if (protocol == "http")
        {
            // DispatchHttpAsync catches all non-cancellation exceptions internally and
            // publishes an AgentTaskError to the result topic, so unobserved exceptions
            // will not be silently lost.
            _ = Task.Run(() => DispatchHttpAsync(agentCard!, agentName, taskRequest, taskId, cts.Token),
                CancellationToken.None);
        }
        else
        {
            var replyTo = $"{options.CallerResultTopic}.{identity.Name}";
            var envelope = taskRequest.ToEnvelope<AgentTaskRequest>(
                source: identity.Name,
                correlationId: taskId,
                replyTo: replyTo);

            await publisher.PublishAsync($"{options.TaskTopic}.{agentName}", envelope, ct);
        }

        a2aActivity?.SetStatus(ActivityStatusCode.Ok);

        A2ADiagnostics.Requests.Add(1,
            new KeyValuePair<string, object?>("rockbot.a2a.target_agent", agentName),
            new KeyValuePair<string, object?>("rockbot.a2a.skill", skill));

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = $"Task dispatched to agent '{agentName}' with task_id: {taskId}. " +
                      $"The result will arrive asynchronously and fold into the conversation.",
            IsError = false
        };
    }

    private async Task DispatchHttpAsync(
        AgentCard agentCard,
        string agentName,
        AgentTaskRequest taskRequest,
        string taskId,
        CancellationToken ct)
    {
        var replyTo = $"{options.CallerResultTopic}.{identity.Name}";

        try
        {
            var httpClient = httpClientFactory.CreateClient();

            // Attach auth header if configured on the agent card
            if (!string.IsNullOrEmpty(agentCard.AuthHeaderName) &&
                !string.IsNullOrEmpty(agentCard.AuthHeaderValueBase64))
            {
                var headerValue = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(agentCard.AuthHeaderValueBase64));
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                    agentCard.AuthHeaderName, headerValue);
            }

            var endpoint = new Uri(agentCard.Url!.TrimEnd('/'));
            logger.LogInformation("Dispatching task {TaskId} to A2A agent '{AgentName}' at {Endpoint}",
                taskId, agentName, endpoint);

            using var httpActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.http_dispatch");
            httpActivity?.SetTag("rockbot.a2a.target_agent", agentName);
            httpActivity?.SetTag("rockbot.a2a.task_id", taskId);
            var httpSw = System.Diagnostics.Stopwatch.StartNew();

            // Use the standard A2A protocol SDK (JSON-RPC 2.0 / v0.3) for HTTP dispatch
            var a2aClient = new A2AProtocol.A2AClient(endpoint, httpClient);
            var messageText = taskRequest.Message.Parts.FirstOrDefault(p => p.Kind == "text")?.Text
                ?? string.Empty;
            var sendParams = new A2AProtocol.MessageSendParams
            {
                Message = new A2AProtocol.AgentMessage
                {
                    Role = A2AProtocol.MessageRole.User,
                    MessageId = taskId,
                    Parts = [new A2AProtocol.TextPart { Text = messageText }]
                },
                Metadata = new Dictionary<string, JsonElement>
                {
                    ["skill"] = JsonSerializer.SerializeToElement(taskRequest.Skill)
                }
            };

            var a2aResponse = await a2aClient.SendMessageAsync(sendParams, ct);

            httpSw.Stop();
            var latencyGrade = httpSw.Elapsed.TotalSeconds > 5 ? "slow" : "fast";
            httpActivity?.SetTag("rockbot.a2a.latency_grade", latencyGrade);
            httpActivity?.SetTag("rockbot.a2a.duration_ms", (long)httpSw.Elapsed.TotalMilliseconds);
            A2ADiagnostics.Duration.Record(httpSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", agentName),
                new KeyValuePair<string, object?>("rockbot.a2a.latency_grade", latencyGrade));

            // Map the A2A protocol response back to RockBot's internal types
            var result = MapA2AResponse(a2aResponse, taskId);
            if (result is null)
            {
                logger.LogWarning("A2A agent '{AgentName}' returned no usable result for task {TaskId}",
                    agentName, taskId);

                var errorResult = new AgentTaskError
                {
                    TaskId = taskId,
                    Code = AgentTaskError.Codes.ExecutionFailed,
                    Message = "Agent returned an empty response."
                };
                var errorEnvelope = errorResult.ToEnvelope<AgentTaskError>(
                    source: agentName,
                    correlationId: taskId);
                await publisher.PublishAsync(replyTo, errorEnvelope, CancellationToken.None);
                return;
            }

            var resultEnvelope = result.ToEnvelope<AgentTaskResult>(
                source: agentName,
                correlationId: taskId);
            await publisher.PublishAsync(replyTo, resultEnvelope, CancellationToken.None);

            logger.LogInformation("A2A task {TaskId} completed (state={State})", taskId, result.State);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("A2A task {TaskId} to agent '{AgentName}' was cancelled", taskId, agentName);
        }
        catch (A2AProtocol.A2AException a2aEx)
        {
            logger.LogError(a2aEx, "A2A protocol error for task {TaskId} to agent '{AgentName}' (code={ErrorCode})",
                taskId, agentName, a2aEx.ErrorCode);

            var error = new AgentTaskError
            {
                TaskId = taskId,
                Code = AgentTaskError.Codes.ExecutionFailed,
                Message = $"A2A protocol error ({a2aEx.ErrorCode}): {a2aEx.Message}",
                IsRetryable = false
            };
            var errorEnvelope = error.ToEnvelope<AgentTaskError>(
                source: agentName,
                correlationId: taskId);
            await publisher.PublishAsync(replyTo, errorEnvelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A2A dispatch failed for task {TaskId} to agent '{AgentName}'",
                taskId, agentName);

            var error = new AgentTaskError
            {
                TaskId = taskId,
                Code = AgentTaskError.Codes.ExecutionFailed,
                Message = $"A2A dispatch failed: {ex.Message}",
                IsRetryable = false
            };
            var errorEnvelope = error.ToEnvelope<AgentTaskError>(
                source: agentName,
                correlationId: taskId);
            await publisher.PublishAsync(replyTo, errorEnvelope, CancellationToken.None);
        }
    }

    /// <summary>
    /// Maps an A2A protocol SDK response to RockBot's internal <see cref="AgentTaskResult"/>.
    /// The V0.3 response is polymorphic: either an <see cref="A2AProtocol.AgentMessage"/>
    /// (immediate reply) or an <see cref="A2AProtocol.AgentTask"/> (task with status).
    /// </summary>
    private static AgentTaskResult? MapA2AResponse(A2AProtocol.A2AResponse response, string taskId)
    {
        if (response is A2AProtocol.AgentMessage msg)
        {
            return new AgentTaskResult
            {
                TaskId = taskId,
                State = AgentTaskState.Completed,
                Message = MapMessage(msg)
            };
        }

        if (response is A2AProtocol.AgentTask task)
        {
            var state = task.Status.State switch
            {
                A2AProtocol.TaskState.Completed => AgentTaskState.Completed,
                A2AProtocol.TaskState.Failed => AgentTaskState.Failed,
                A2AProtocol.TaskState.Canceled => AgentTaskState.Canceled,
                A2AProtocol.TaskState.Working => AgentTaskState.Working,
                A2AProtocol.TaskState.InputRequired => AgentTaskState.InputRequired,
                A2AProtocol.TaskState.Submitted => AgentTaskState.Submitted,
                _ => AgentTaskState.Completed
            };

            return new AgentTaskResult
            {
                TaskId = taskId,
                ContextId = task.ContextId,
                State = state,
                Message = task.Status.Message is { } statusMsg ? MapMessage(statusMsg) : null
            };
        }

        return null;
    }

    private static AgentMessage MapMessage(A2AProtocol.AgentMessage msg) => new()
    {
        Role = msg.Role == A2AProtocol.MessageRole.Agent ? "assistant" : "user",
        Parts = msg.Parts.Select(p => new AgentMessagePart
        {
            Kind = p is A2AProtocol.TextPart ? "text" : "data",
            Text = p is A2AProtocol.TextPart tp ? tp.Text : null,
            Data = p is A2AProtocol.DataPart dp ? JsonSerializer.Serialize(dp.Data) : null
        }).ToList()
    };

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
