using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

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
            _ = Task.Run(() => DispatchHttpAsync(agentCard!.Url, agentName, taskRequest, taskId, cts.Token),
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
        string agentUrl,
        string agentName,
        AgentTaskRequest taskRequest,
        string taskId,
        CancellationToken ct)
    {
        var replyTo = $"{options.CallerResultTopic}.{identity.Name}";

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            var endpoint = agentUrl.TrimEnd('/') + "/tasks/send";

            logger.LogInformation("Dispatching task {TaskId} to HTTP agent '{AgentName}' at {Endpoint}",
                taskId, agentName, endpoint);

            using var httpActivity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.http_dispatch");
            httpActivity?.SetTag("rockbot.a2a.target_agent", agentName);
            httpActivity?.SetTag("rockbot.a2a.task_id", taskId);
            var httpSw = System.Diagnostics.Stopwatch.StartNew();

            var response = await httpClient.PostAsJsonAsync(endpoint, taskRequest, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AgentTaskResult>(JsonOptions, ct);

            httpSw.Stop();
            var latencyGrade = httpSw.Elapsed.TotalSeconds > 5 ? "slow" : "fast";
            httpActivity?.SetTag("rockbot.a2a.latency_grade", latencyGrade);
            httpActivity?.SetTag("rockbot.a2a.duration_ms", (long)httpSw.Elapsed.TotalMilliseconds);
            A2ADiagnostics.Duration.Record(httpSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", agentName),
                new KeyValuePair<string, object?>("rockbot.a2a.latency_grade", latencyGrade));
            if (result is null)
            {
                logger.LogWarning("HTTP agent '{AgentName}' returned null result for task {TaskId}",
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

            logger.LogInformation("HTTP task {TaskId} completed (state={State})", taskId, result.State);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("HTTP task {TaskId} to agent '{AgentName}' was cancelled", taskId, agentName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HTTP dispatch failed for task {TaskId} to agent '{AgentName}'",
                taskId, agentName);

            var error = new AgentTaskError
            {
                TaskId = taskId,
                Code = AgentTaskError.Codes.ExecutionFailed,
                Message = $"HTTP dispatch failed: {ex.Message}",
                IsRetryable = false
            };
            var errorEnvelope = error.ToEnvelope<AgentTaskError>(
                source: agentName,
                correlationId: taskId);
            await publisher.PublishAsync(replyTo, errorEnvelope, CancellationToken.None);
        }
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
