using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using A2AV03 = A2A.V0_3;
using A2AV1 = A2A;

namespace RockBot.A2A;

/// <summary>
/// Publishes an <see cref="AgentTaskRequest"/> to a target agent and registers the
/// pending task in <see cref="A2ATaskTracker"/>. Returns the task ID immediately.
/// Supports both queue-based (RabbitMQ) and HTTP-based transport. HTTP transport is
/// used when the target agent's <see cref="AgentCard"/> has a non-empty <c>Url</c>.
/// Protocol version is detected from <see cref="AgentCard.ProtocolVersion"/>:
/// "1.0" uses the A2A v1 SDK, anything else (including null) uses v0.3.
///
/// For HTTP transport, non-terminal responses (Working, Submitted) trigger a
/// polling loop via GetTask with exponential backoff. InputRequired responses
/// trigger a trust-gated follow-up loop via <see cref="InputRequiredHandler"/>.
/// </summary>
internal sealed class InvokeAgentExecutor(
    IMessagePublisher publisher,
    A2ATaskTracker tracker,
    IAgentDirectory directory,
    A2AOptions options,
    AgentIdentity identity,
    IHttpClientFactory httpClientFactory,
    InputRequiredHandler inputRequiredHandler,
    ILogger<InvokeAgentExecutor> logger) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns true when the agent card indicates A2A protocol v1.
    /// </summary>
    internal static bool IsV1(AgentCard? card) =>
        card?.ProtocolVersion is { } v &&
        (v.StartsWith("1.", StringComparison.Ordinal) || v == "1");

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

        // Reject self-invocation — the LLM sometimes uses its own identity name
        // instead of the target agent's name from the directory.
        if (agentName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
            return Error(request,
                $"Cannot invoke yourself ('{agentName}'). " +
                $"Use list_known_agents to find the correct external agent name.");

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
        var a2aVersion = IsV1(agentCard) ? "1.0" : "0.3";

        using var a2aActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.invoke");
        a2aActivity?.SetTag("rockbot.a2a.target_agent", agentName);
        a2aActivity?.SetTag("rockbot.a2a.skill", skill);
        a2aActivity?.SetTag("rockbot.a2a.task_id", taskId);
        a2aActivity?.SetTag("rockbot.a2a.protocol", protocol);
        a2aActivity?.SetTag("rockbot.a2a.a2a_version", a2aVersion);
        a2aActivity?.SetTag("rockbot.a2a.session_id", primarySessionId);

        if (protocol == "http")
        {
            // DispatchHttpAsync catches all non-cancellation exceptions internally and
            // publishes an AgentTaskError to the result topic, so unobserved exceptions
            // will not be silently lost.
            _ = Task.Run(() => DispatchHttpAsync(agentCard!, agentName, taskRequest, taskId, pending, cts.Token),
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
        PendingA2ATask pending,
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
            var useV1 = IsV1(agentCard);
            logger.LogInformation(
                "Dispatching task {TaskId} to A2A agent '{AgentName}' at {Endpoint} (protocol={Version})",
                taskId, agentName, endpoint, useV1 ? "v1" : "v0.3");

            using var httpActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.http_dispatch");
            httpActivity?.SetTag("rockbot.a2a.target_agent", agentName);
            httpActivity?.SetTag("rockbot.a2a.task_id", taskId);
            httpActivity?.SetTag("rockbot.a2a.a2a_version", useV1 ? "1.0" : "0.3");
            httpActivity?.SetTag("rockbot.a2a.session_id", pending.PrimarySessionId);
            httpActivity?.SetTag("rockbot.a2a.correlation_id", taskId);
            var httpSw = Stopwatch.StartNew();

            var messageText = taskRequest.Message.Parts.FirstOrDefault(p => p.Kind == "text")?.Text
                ?? string.Empty;

            AgentTaskResult? result;
            if (useV1)
                result = await DispatchV1Async(httpClient, endpoint, taskRequest, taskId, messageText, pending, ct);
            else
                result = await DispatchV03Async(httpClient, endpoint, taskRequest, taskId, messageText, pending, ct);

            httpSw.Stop();
            var latencyGrade = httpSw.Elapsed.TotalSeconds > 5 ? "slow" : "fast";
            httpActivity?.SetTag("rockbot.a2a.latency_grade", latencyGrade);
            httpActivity?.SetTag("rockbot.a2a.duration_ms", (long)httpSw.Elapsed.TotalMilliseconds);
            httpActivity?.SetTag("rockbot.a2a.context_id", pending.ContextId);
            A2ADiagnostics.Duration.Record(httpSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", agentName),
                new KeyValuePair<string, object?>("rockbot.a2a.latency_grade", latencyGrade));

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
        catch (A2AV03.A2AException a2aEx)
        {
            logger.LogError(a2aEx, "A2A v0.3 protocol error for task {TaskId} to agent '{AgentName}' (code={ErrorCode})",
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
        catch (A2AV1.A2AException a2aEx)
        {
            logger.LogError(a2aEx, "A2A v1 protocol error for task {TaskId} to agent '{AgentName}' (code={ErrorCode})",
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

    // ── V0.3 dispatch ────────────────────────────────────────────────────────────

    private async Task<AgentTaskResult?> DispatchV03Async(
        HttpClient httpClient,
        Uri endpoint,
        AgentTaskRequest taskRequest,
        string taskId,
        string messageText,
        PendingA2ATask pending,
        CancellationToken ct)
    {
        var a2aClient = new A2AV03.A2AClient(endpoint, httpClient);
        string? contextId = null;
        var repetitionDetector = new InputRequiredRepetitionDetector(options.InputRequiredRepetitionThreshold);

        // Initial send
        var sendParams = BuildV03SendParams(taskId, messageText, contextId, taskRequest.Skill);
        var a2aResponse = await a2aClient.SendMessageAsync(sendParams, ct);
        var result = MapV03Response(a2aResponse, taskId);

        while (result is not null && !ct.IsCancellationRequested)
        {
            contextId ??= result.ContextId;
            pending.ContextId ??= contextId;

            // Terminal states — done
            if (result.State is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Canceled)
                return result;

            // Working/Submitted — poll until non-working
            if (result.State is AgentTaskState.Working or AgentTaskState.Submitted)
            {
                await PublishStatusUpdateAsync(result, pending.TargetAgent, taskId, ct);
                result = await PollV03UntilNonWorkingAsync(a2aClient, taskId, pending, ct);
                continue;
            }

            // InputRequired — follow-up loop
            if (result.State == AgentTaskState.InputRequired)
            {
                pending.InputRequiredRound++;
                if (pending.InputRequiredRound > options.MaxInputRequiredRounds)
                {
                    logger.LogWarning(
                        "A2A task {TaskId} exceeded max InputRequired rounds ({Max})",
                        taskId, options.MaxInputRequiredRounds);
                    A2ADiagnostics.InputRequiredBreaks.Add(1,
                        new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
                        new KeyValuePair<string, object?>("rockbot.a2a.reason", "max_rounds"));
                    return MakeLoopExceededResult(taskId, contextId, "max rounds exceeded");
                }

                var questionText = ExtractResultText(result);
                logger.LogInformation(
                    "A2A task {TaskId} from '{AgentName}' requires input (round {Round})",
                    taskId, pending.TargetAgent, pending.InputRequiredRound);

                var followUp = await inputRequiredHandler.HandleAsync(
                    new InputRequiredContext
                    {
                        TaskId = taskId,
                        ContextId = contextId,
                        TargetAgent = pending.TargetAgent,
                        Skill = pending.Skill,
                        QuestionText = questionText,
                        PrimarySessionId = pending.PrimarySessionId,
                        Round = pending.InputRequiredRound
                    }, ct);

                if (repetitionDetector.Track(questionText, followUp.ResponseText))
                {
                    logger.LogWarning(
                        "A2A task {TaskId} InputRequired loop stuck (repeated {Threshold}x)",
                        taskId, options.InputRequiredRepetitionThreshold);
                    A2ADiagnostics.InputRequiredBreaks.Add(1,
                        new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
                        new KeyValuePair<string, object?>("rockbot.a2a.reason", "repetition"));
                    return MakeLoopExceededResult(taskId, contextId, "conversation stuck in a loop");
                }

                logger.LogInformation(
                    "A2A InputRequired follow-up sent for task {TaskId} round {Round} (autonomous={Autonomous})",
                    taskId, pending.InputRequiredRound, followUp.WasAutonomous);

                // Send follow-up with contextId
                sendParams = BuildV03SendParams(taskId, followUp.ResponseText, contextId, taskRequest.Skill);
                a2aResponse = await a2aClient.SendMessageAsync(sendParams, ct);
                result = MapV03Response(a2aResponse, taskId);
                continue;
            }

            break; // Unknown state
        }

        return result;
    }

    private static A2AV03.MessageSendParams BuildV03SendParams(
        string taskId, string messageText, string? contextId, string skill)
    {
        return new A2AV03.MessageSendParams
        {
            Message = new A2AV03.AgentMessage
            {
                Role = A2AV03.MessageRole.User,
                MessageId = taskId,
                ContextId = contextId,
                Parts = [new A2AV03.TextPart { Text = messageText }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement(skill)
            }
        };
    }

    private async Task<AgentTaskResult?> PollV03UntilNonWorkingAsync(
        A2AV03.A2AClient a2aClient,
        string taskId,
        PendingA2ATask pending,
        CancellationToken ct)
    {
        using var pollActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.poll_loop");
        pollActivity?.SetTag("rockbot.a2a.task_id", taskId);
        pollActivity?.SetTag("rockbot.a2a.target_agent", pending.TargetAgent);
        pollActivity?.SetTag("rockbot.a2a.session_id", pending.PrimarySessionId);

        var delay = options.PollingInitialDelay;
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(delay, ct);
            attempt++;

            logger.LogInformation(
                "Polling task {TaskId} to '{AgentName}' (attempt {Attempt}, delay {DelayMs}ms)",
                taskId, pending.TargetAgent, attempt, (long)delay.TotalMilliseconds);

            A2ADiagnostics.PollingAttempts.Add(1,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent));

            var task = await a2aClient.GetTaskAsync(taskId, ct);
            if (task is null)
            {
                delay = NextDelay(delay);
                continue;
            }

            var result = MapV03TaskResponse(task, taskId);
            if (result is null)
            {
                delay = NextDelay(delay);
                continue;
            }

            pending.ContextId ??= result.ContextId;

            if (result.State is AgentTaskState.Working or AgentTaskState.Submitted)
            {
                await PublishStatusUpdateAsync(result, pending.TargetAgent, taskId, ct);
                delay = NextDelay(delay);
                continue;
            }

            pollActivity?.SetTag("rockbot.a2a.total_polls", attempt);
            return result;
        }

        return null;
    }

    /// <summary>
    /// Maps an A2A v0.3 protocol SDK response to RockBot's internal <see cref="AgentTaskResult"/>.
    /// The V0.3 response is polymorphic: either an <see cref="A2AV03.AgentMessage"/>
    /// (immediate reply) or an <see cref="A2AV03.AgentTask"/> (task with status).
    /// </summary>
    internal static AgentTaskResult? MapV03Response(A2AV03.A2AResponse response, string taskId)
    {
        if (response is A2AV03.AgentMessage msg)
        {
            return new AgentTaskResult
            {
                TaskId = taskId,
                State = AgentTaskState.Completed,
                Message = MapV03Message(msg)
            };
        }

        if (response is A2AV03.AgentTask task)
        {
            return MapV03TaskResponse(task, taskId);
        }

        return null;
    }

    internal static AgentTaskResult? MapV03TaskResponse(A2AV03.AgentTask task, string taskId)
    {
        var state = task.Status.State switch
        {
            A2AV03.TaskState.Completed => AgentTaskState.Completed,
            A2AV03.TaskState.Failed => AgentTaskState.Failed,
            A2AV03.TaskState.Canceled => AgentTaskState.Canceled,
            A2AV03.TaskState.Working => AgentTaskState.Working,
            A2AV03.TaskState.InputRequired => AgentTaskState.InputRequired,
            A2AV03.TaskState.Submitted => AgentTaskState.Submitted,
            _ => AgentTaskState.Completed
        };

        return new AgentTaskResult
        {
            TaskId = taskId,
            ContextId = task.ContextId,
            State = state,
            Message = task.Status.Message is { } statusMsg ? MapV03Message(statusMsg) : null
        };
    }

    private static AgentMessage MapV03Message(A2AV03.AgentMessage msg) => new()
    {
        Role = msg.Role == A2AV03.MessageRole.Agent ? "assistant" : "user",
        Parts = msg.Parts.Select(p => new AgentMessagePart
        {
            Kind = p is A2AV03.TextPart ? "text" : "data",
            Text = p is A2AV03.TextPart tp ? tp.Text : null,
            Data = p is A2AV03.DataPart dp ? JsonSerializer.Serialize(dp.Data) : null
        }).ToList()
    };

    // ── V1 dispatch ──────────────────────────────────────────────────────────────

    private async Task<AgentTaskResult?> DispatchV1Async(
        HttpClient httpClient,
        Uri endpoint,
        AgentTaskRequest taskRequest,
        string taskId,
        string messageText,
        PendingA2ATask pending,
        CancellationToken ct)
    {
        var a2aClient = new A2AV1.A2AClient(endpoint, httpClient);
        string? contextId = null;
        var repetitionDetector = new InputRequiredRepetitionDetector(options.InputRequiredRepetitionThreshold);

        // Initial send
        var sendRequest = BuildV1SendRequest(taskId, messageText, contextId, taskRequest.Skill);
        var a2aResponse = await a2aClient.SendMessageAsync(sendRequest, ct);
        var result = MapV1Response(a2aResponse, taskId);

        while (result is not null && !ct.IsCancellationRequested)
        {
            contextId ??= result.ContextId;
            pending.ContextId ??= contextId;

            // Terminal states — done
            if (result.State is AgentTaskState.Completed or AgentTaskState.Failed or AgentTaskState.Canceled)
                return result;

            // Working/Submitted — poll until non-working
            if (result.State is AgentTaskState.Working or AgentTaskState.Submitted)
            {
                await PublishStatusUpdateAsync(result, pending.TargetAgent, taskId, ct);
                result = await PollV1UntilNonWorkingAsync(a2aClient, taskId, pending, ct);
                continue;
            }

            // InputRequired — follow-up loop
            if (result.State == AgentTaskState.InputRequired)
            {
                pending.InputRequiredRound++;
                if (pending.InputRequiredRound > options.MaxInputRequiredRounds)
                {
                    logger.LogWarning(
                        "A2A task {TaskId} exceeded max InputRequired rounds ({Max})",
                        taskId, options.MaxInputRequiredRounds);
                    A2ADiagnostics.InputRequiredBreaks.Add(1,
                        new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
                        new KeyValuePair<string, object?>("rockbot.a2a.reason", "max_rounds"));
                    return MakeLoopExceededResult(taskId, contextId, "max rounds exceeded");
                }

                var questionText = ExtractResultText(result);
                logger.LogInformation(
                    "A2A task {TaskId} from '{AgentName}' requires input (round {Round})",
                    taskId, pending.TargetAgent, pending.InputRequiredRound);

                var followUp = await inputRequiredHandler.HandleAsync(
                    new InputRequiredContext
                    {
                        TaskId = taskId,
                        ContextId = contextId,
                        TargetAgent = pending.TargetAgent,
                        Skill = pending.Skill,
                        QuestionText = questionText,
                        PrimarySessionId = pending.PrimarySessionId,
                        Round = pending.InputRequiredRound
                    }, ct);

                if (repetitionDetector.Track(questionText, followUp.ResponseText))
                {
                    logger.LogWarning(
                        "A2A task {TaskId} InputRequired loop stuck (repeated {Threshold}x)",
                        taskId, options.InputRequiredRepetitionThreshold);
                    A2ADiagnostics.InputRequiredBreaks.Add(1,
                        new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
                        new KeyValuePair<string, object?>("rockbot.a2a.reason", "repetition"));
                    return MakeLoopExceededResult(taskId, contextId, "conversation stuck in a loop");
                }

                logger.LogInformation(
                    "A2A InputRequired follow-up sent for task {TaskId} round {Round} (autonomous={Autonomous})",
                    taskId, pending.InputRequiredRound, followUp.WasAutonomous);

                // Send follow-up with contextId
                sendRequest = BuildV1SendRequest(taskId, followUp.ResponseText, contextId, taskRequest.Skill);
                a2aResponse = await a2aClient.SendMessageAsync(sendRequest, ct);
                result = MapV1Response(a2aResponse, taskId);
                continue;
            }

            break; // Unknown state
        }

        return result;
    }

    private static A2AV1.SendMessageRequest BuildV1SendRequest(
        string taskId, string messageText, string? contextId, string skill)
    {
        return new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = taskId,
                ContextId = contextId,
                Parts = [new A2AV1.Part { Text = messageText }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement(skill)
            }
        };
    }

    private async Task<AgentTaskResult?> PollV1UntilNonWorkingAsync(
        A2AV1.A2AClient a2aClient,
        string taskId,
        PendingA2ATask pending,
        CancellationToken ct)
    {
        using var pollActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.poll_loop");
        pollActivity?.SetTag("rockbot.a2a.task_id", taskId);
        pollActivity?.SetTag("rockbot.a2a.target_agent", pending.TargetAgent);
        pollActivity?.SetTag("rockbot.a2a.session_id", pending.PrimarySessionId);

        var delay = options.PollingInitialDelay;
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(delay, ct);
            attempt++;

            logger.LogInformation(
                "Polling task {TaskId} to '{AgentName}' (attempt {Attempt}, delay {DelayMs}ms)",
                taskId, pending.TargetAgent, attempt, (long)delay.TotalMilliseconds);

            A2ADiagnostics.PollingAttempts.Add(1,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent));

            var getRequest = new A2AV1.GetTaskRequest { Id = taskId };
            var task = await a2aClient.GetTaskAsync(getRequest, ct);
            if (task is null)
            {
                delay = NextDelay(delay);
                continue;
            }

            var result = MapV1TaskResponse(task, taskId);
            if (result is null)
            {
                delay = NextDelay(delay);
                continue;
            }

            pending.ContextId ??= result.ContextId;

            if (result.State is AgentTaskState.Working or AgentTaskState.Submitted)
            {
                await PublishStatusUpdateAsync(result, pending.TargetAgent, taskId, ct);
                delay = NextDelay(delay);
                continue;
            }

            pollActivity?.SetTag("rockbot.a2a.total_polls", attempt);
            return result;
        }

        return null;
    }

    /// <summary>
    /// Maps an A2A v1 protocol SDK response to RockBot's internal <see cref="AgentTaskResult"/>.
    /// The V1 response uses <see cref="A2AV1.SendMessageResponseCase"/> to discriminate
    /// between a <see cref="A2AV1.Message"/> and an <see cref="A2AV1.AgentTask"/>.
    /// </summary>
    internal static AgentTaskResult? MapV1Response(A2AV1.SendMessageResponse response, string taskId)
    {
        switch (response.PayloadCase)
        {
            case A2AV1.SendMessageResponseCase.Message when response.Message is { } msg:
                return new AgentTaskResult
                {
                    TaskId = taskId,
                    State = AgentTaskState.Completed,
                    Message = MapV1Message(msg)
                };

            case A2AV1.SendMessageResponseCase.Task when response.Task is { } task:
                return MapV1TaskResponse(task, taskId);

            default:
                return null;
        }
    }

    internal static AgentTaskResult? MapV1TaskResponse(A2AV1.AgentTask task, string taskId)
    {
        var state = task.Status.State switch
        {
            A2AV1.TaskState.Completed => AgentTaskState.Completed,
            A2AV1.TaskState.Failed => AgentTaskState.Failed,
            A2AV1.TaskState.Canceled => AgentTaskState.Canceled,
            A2AV1.TaskState.Working => AgentTaskState.Working,
            A2AV1.TaskState.InputRequired => AgentTaskState.InputRequired,
            A2AV1.TaskState.Submitted => AgentTaskState.Submitted,
            A2AV1.TaskState.Rejected => AgentTaskState.Failed,
            A2AV1.TaskState.AuthRequired => AgentTaskState.InputRequired,
            _ => AgentTaskState.Completed
        };

        return new AgentTaskResult
        {
            TaskId = taskId,
            ContextId = task.ContextId,
            State = state,
            Message = task.Status.Message is { } statusMsg ? MapV1Message(statusMsg) : null
        };
    }

    private static AgentMessage MapV1Message(A2AV1.Message msg) => new()
    {
        Role = msg.Role == A2AV1.Role.Agent ? "assistant" : "user",
        Parts = msg.Parts.Select(p => new AgentMessagePart
        {
            Kind = p.ContentCase == A2AV1.PartContentCase.Text ? "text" : "data",
            Text = p.ContentCase == A2AV1.PartContentCase.Text ? p.Text : null,
            Data = p.ContentCase == A2AV1.PartContentCase.Data ? JsonSerializer.Serialize(p.Data) : null
        }).ToList()
    };

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task PublishStatusUpdateAsync(
        AgentTaskResult result, string agentName, string correlationId, CancellationToken ct)
    {
        var statusUpdate = new AgentTaskStatusUpdate
        {
            TaskId = result.TaskId,
            ContextId = result.ContextId,
            State = result.State,
            Message = result.Message
        };
        var envelope = statusUpdate.ToEnvelope<AgentTaskStatusUpdate>(
            source: agentName,
            correlationId: correlationId);
        await publisher.PublishAsync(options.StatusTopic, envelope, ct);
    }

    private TimeSpan NextDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(
            Math.Min(current.TotalMilliseconds * 2, options.PollingMaxDelay.TotalMilliseconds));

    private static string ExtractResultText(AgentTaskResult result) =>
        result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? "(no message)";

    private static AgentTaskResult MakeLoopExceededResult(string taskId, string? contextId, string reason) =>
        new()
        {
            TaskId = taskId,
            ContextId = contextId,
            State = AgentTaskState.Failed,
            Message = new AgentMessage
            {
                Role = "assistant",
                Parts = [new AgentMessagePart
                {
                    Kind = "text",
                    Text = $"The multi-turn conversation was terminated: {reason}. " +
                           "Consider breaking the request into smaller parts or providing more specific instructions."
                }]
            }
        };

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
