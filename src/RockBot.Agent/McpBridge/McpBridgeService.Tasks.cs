using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.Handback;
using RockBot.Agent.McpBridge.Tasks;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// Background tasks (the MCP Tasks extension, SEP-2663): a call to a server with
/// <c>"tasks": true</c> from a caller that can take a later result starts as a task and returns
/// "started" at once. The bridge polls the task on its own lifetime, hands any question the task
/// asks to the session that started it, and delivers the result to that session as a new turn.
/// The task lives on its server, so it survives a restart of the agent: the task ledger says what
/// to poll again. See <c>design/mcp-bridge.md</c>, "Long-running tools".
/// </summary>
public sealed partial class McpBridgeService
{
    /// <summary>Longest tool description kept in a task's ledger entry.</summary>
    private const int TaskToolDescriptionMaxChars = 300;

    /// <summary>How long a task's result stays in the session's working memory.</summary>
    private static readonly TimeSpan TaskResultTtl = TimeSpan.FromMinutes(60);

    /// <summary>How long a best-effort <c>tasks/cancel</c> may take.</summary>
    private static readonly TimeSpan TaskCancelTimeout = TimeSpan.FromSeconds(10);

    private readonly McpTaskLedger _taskLedger;
    private readonly McpTaskRegistry _tasks;
    private readonly TimeProvider _time;

    /// <summary>
    /// Whether this call runs as a background task: the server's entry turns tasks on, it speaks
    /// the 2026-07-28 protocol, and the caller's session can take a later result.
    /// </summary>
    private static bool WantsTask(MessageEnvelope envelope, ToolInvokeRequest request, ConnectedServer server) =>
        server.Config.RunsTasks
        && IsJuly2026OrLater(server.Client.NegotiatedProtocolVersion)
        && envelope.Headers.TryGetValue(McpHeaders.Tasks, out var capable)
        && string.Equals(capable, McpHeaders.TasksCapable, StringComparison.Ordinal)
        && McpTaskContext.IsBackgroundCapableSession(request.SessionId);

    private static bool IsHandbackCapable(MessageEnvelope envelope) =>
        envelope.Headers.TryGetValue(McpHeaders.Handback, out var capable)
        && string.Equals(capable, McpHeaders.HandbackCapable, StringComparison.Ordinal);

    /// <summary>Takes a place under the caps for a task <paramref name="request"/>'s session is about to start.</summary>
    private RunningMcpTask? ReserveTask(ToolInvokeRequest request, out string? refusal)
    {
        var sessionId = request.SessionId!;
        var maxPerSession = Math.Max(1, _options.MaxTasksPerSession);
        var maxTotal = Math.Max(1, _options.MaxTasks);
        var task = _tasks.TryReserve(NewTaskId(), sessionId, maxPerSession, maxTotal, force: false, out var scope);
        if (task is not null)
        {
            refusal = null;
            return task;
        }

        McpTaskDiagnostics.Record(request.ToolName, "refused");
        refusal = McpTaskText.CapReached(scope!, scope == "session" ? maxPerSession : maxTotal, _tasks.ForSession(sessionId));
        _logger.LogWarning("MCP {Tool} not started as a background task for {Session}: the {Scope} cap is reached",
            request.ToolName, sessionId, scope);
        return null;
    }

    /// <summary>Frees a reservation that never became a task (the call failed, or the server ran it synchronously).</summary>
    private void ReleaseUnstartedTask(RunningMcpTask task)
    {
        if (task.Entry is null)
            _tasks.Remove(task.TaskId);
    }

    /// <summary>
    /// Starts the call as a task. When the server creates one, records it, starts polling it, and
    /// returns the "started" result; when the server answers at once (its tool doesn't run as a
    /// task), returns that answer as an ordinary result.
    /// </summary>
    private async Task<CallToolResult> StartAsTaskAsync(InvocationState state, ConnectedServer server, CancellationToken ct)
    {
        var task = state.Task!;
        var request = state.Request;

        // Already started (a failure after that sent the call round again): never start it twice.
        if (task.Entry is { } existing)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = McpTaskText.Started(task.TaskId, existing.Server, existing.Tool) }],
            };
        }

        var started = await server.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = request.ToolName,
            Arguments = ToJsonArguments(state.Arguments),
        }, ct);

        if (!started.IsTask)
        {
            _logger.LogInformation("MCP {Server}/{Tool} answered at once rather than as a background task",
                server.Name, request.ToolName);
            return started.Result!;
        }

        var created = started.TaskCreated!;
        var now = _time.GetUtcNow();
        var timeout = TimeSpan.FromMinutes(Math.Max(1, server.Config.TaskTimeoutMinutes ?? _options.DefaultTaskTimeoutMinutes));
        var entry = new McpTaskEntry
        {
            TaskId = task.TaskId,
            ServerTaskId = created.TaskId,
            Status = McpTaskEntryStatus.Running,
            SessionId = task.SessionId,
            Server = server.Name,
            ServerId = server.Config.Id,
            Tool = request.ToolName,
            ToolDescription = Truncate(FlattenOrNull(state.ToolDescription), TaskToolDescriptionMaxChars),
            Arguments = LlmElicitationResponder.RedactArguments(request.Arguments),
            TriggeredBy = await FindTaskTriggerAsync(task.SessionId, now, ct),
            HandbackCapable = state.TaskHandbackCapable,
            StartedAt = now,
            Deadline = now + timeout,
            PollIntervalMs = created.PollIntervalMs,
        };

        // Write-ahead: the agent hears of no task the ledger doesn't hold, or a restart would leave
        // it running on the server with nobody polling it.
        try
        {
            await _taskLedger.AddAsync(entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not record background task {Server}/{Tool} in the task ledger; cancelling it",
                server.Name, request.ToolName);
            await TryCancelOnServerAsync(server.Client, created.TaskId, server.Name);

            // A result, not an exception: an exception would have the call retried on a new connection.
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"[rockbot] {request.ToolName} on \"{server.Name}\" was not started: " +
                    "the agent couldn't record it as a background task. Try again later." }],
            };
        }

        LaunchTask(task, entry, request.Arguments, resumed: false);
        McpTaskDiagnostics.Record(server.Name, "started");
        _logger.LogInformation("← MCP {Server}/{Tool} started as background task {TaskId} (server task {ServerTaskId}) for {Session}",
            server.Name, request.ToolName, task.TaskId, created.TaskId, task.SessionId);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = McpTaskText.Started(task.TaskId, server.Name, request.ToolName) }],
        };
    }

    /// <summary>Sets a task's deadline and question scope, and starts its poll loop on the bridge's lifetime.</summary>
    private void LaunchTask(RunningMcpTask task, McpTaskEntry entry, string? rawArguments, bool resumed)
    {
        task.Entry = entry;

        // The deadline runs on the bridge's clock, so it holds across a restart (it is in the ledger).
        var remaining = entry.Deadline - _time.GetUtcNow();
        var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time);
        task.Cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);

        // A question the task asks goes to its session when that session can answer one: a user
        // conversation whose run had mcp_answer. A subagent can't be reached mid-run, so its tasks'
        // questions get the server's responder, as a subagent's parked calls would without hand-back.
        IMcpHandbackChannel? channel = entry.HandbackCapable && HandbackSessions.IsUserSession(entry.SessionId)
            ? new TaskHandbackCall(
                _handback,
                new HandbackCallInfo(entry.Server, entry.Tool, entry.ToolDescription, rawArguments, entry.SessionId,
                    entry.StartedAt, HandbackTtlFor(entry.Server)),
                entry.TaskId,
                (question, ct) => DeliverTaskQuestionAsync(task, question, ct))
            : null;
        task.Scope = McpElicitationCoordinator.BeginDetachedCall(entry.Tool, rawArguments, entry.SessionId, channel);

        TrackParked(RunTaskAsync(task, deadline, resumed));
    }

    /// <summary>The TTL of a question handed back by <paramref name="serverName"/>, from its own policy.</summary>
    private TimeSpan HandbackTtlFor(string serverName) =>
        _connections.TryGetConfig(serverName, out var config) && config.Elicitation is { } policy
            ? policy.HandbackTtl
            : new McpElicitationConfig().HandbackTtl;

    /// <summary>
    /// Polls one task until it ends, answering its questions on the way, then delivers its ending.
    /// A restart (the bridge stopping) leaves the task running on the server and in the ledger.
    /// </summary>
    private async Task RunTaskAsync(RunningMcpTask task, CancellationTokenSource deadline, bool resumed)
    {
        var entry = task.Entry!;
        var cts = task.Cts!;
        var ct = cts.Token;
        var delay = PollDelay(entry.PollIntervalMs);
        var failures = 0;
        var pollNow = resumed;
        var limit = Math.Max(1, _options.TaskPollFailureLimit);

        try
        {
            while (true)
            {
                if (!pollNow)
                    await Task.Delay(delay, _time, ct);
                pollNow = false;

                Dictionary<string, InputRequest>? questions = null;
                McpElicitationCoordinator? coordinator = null;
                var serverName = entry.Server;

                using (var lease = LeaseTaskServer(entry, out var configured))
                {
                    if (lease is null)
                    {
                        if (!configured)
                        {
                            await EndTaskAsync(task, McpTaskEntryStatus.Lost,
                                $"its MCP server \"{entry.Server}\" is no longer configured");
                            return;
                        }

                        if (++failures >= limit)
                        {
                            await EndTaskAsync(task, McpTaskEntryStatus.Lost,
                                $"its MCP server \"{entry.Server}\" couldn't be reached to check on it");
                            return;
                        }

                        delay = Backoff(delay);
                        continue;
                    }

                    serverName = lease.Server.Name;
                    GetTaskResult status;
                    try
                    {
                        status = await lease.Server.Client.GetTaskAsync(entry.ServerTaskId, ct);
                        failures = 0;
                    }
                    catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
                    {
                        // The server answered and doesn't know the task: an in-memory task store
                        // that restarted, or a task past its time-to-live.
                        _logger.LogWarning("MCP {Server} no longer knows background task {TaskId}: {Message}",
                            serverName, task.TaskId, ex.Message);
                        await EndTaskAsync(task, McpTaskEntryStatus.Lost,
                            "the server no longer has it (it may have restarted, or kept the task only for a limited time)");
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Polling background task {TaskId} on {Server} failed ({Failures}/{Limit})",
                            task.TaskId, serverName, failures + 1, limit);
                        if (++failures >= limit)
                        {
                            await EndTaskAsync(task, McpTaskEntryStatus.Lost,
                                $"its MCP server \"{serverName}\" kept failing to report on it ({ex.Message})");
                            return;
                        }

                        delay = Backoff(delay);
                        continue;
                    }

                    if (status.PollIntervalMs is { } suggested)
                        entry.PollIntervalMs = suggested;
                    delay = PollDelay(entry.PollIntervalMs);

                    switch (status)
                    {
                        case CompletedTaskResult completed:
                            await DeliverCompletedAsync(task, lease.Server, completed, ct);
                            return;

                        case FailedTaskResult failed:
                            await EndTaskAsync(task, McpTaskEntryStatus.Failed, DescribeTaskError(failed.Error));
                            return;

                        case CancelledTaskResult:
                            await EndTaskAsync(task, McpTaskEntryStatus.Cancelled, task.CancelReason ?? "the server cancelled it");
                            return;

                        case InputRequiredTaskResult input:
                            questions = (input.InputRequests ?? new Dictionary<string, InputRequest>())
                                .Where(q => !entry.ResolvedInputKeys.Contains(q.Key, StringComparer.Ordinal))
                                .ToDictionary(q => q.Key, q => q.Value, StringComparer.Ordinal);
                            coordinator = lease.Server.Elicitation;
                            break;
                    }
                }

                // Answered without holding the lease: a hand-back can wait as long as its TTL, and a
                // reconnect meanwhile must be free to retire the connection.
                if (questions is { Count: > 0 })
                {
                    await AnswerTaskQuestionsAsync(task, serverName, questions, coordinator, ct);
                    pollNow = true;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (task.CancelReason is null && _lifetime.IsCancellationRequested)
            {
                // The bridge is stopping. The task goes on on its server; the next start polls it.
                return;
            }

            var timedOut = task.CancelReason is null && deadline.IsCancellationRequested;
            if (!task.CancelledOnServer)
                await TryCancelOnServerAsync(entry);
            await EndTaskAsync(task,
                timedOut ? McpTaskEntryStatus.TimedOut : McpTaskEntryStatus.Cancelled,
                timedOut ? null : task.CancelReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background task {TaskId} ({Server}/{Tool}) failed in the bridge", task.TaskId, entry.Server, entry.Tool);
            await TryCancelOnServerAsync(entry);
            await EndTaskAsync(task, McpTaskEntryStatus.Lost, $"the agent lost track of it ({ex.Message})");
        }
        finally
        {
            deadline.Dispose();
        }
    }

    /// <summary>
    /// Answers a task's new input requests through its server's elicitation policy — the same
    /// declines, defaults, caps and responder a synchronous call gets, or a hand-back to the
    /// session — and sends the answers with <c>tasks/update</c>.
    /// </summary>
    private async Task AnswerTaskQuestionsAsync(
        RunningMcpTask task, string serverName, Dictionary<string, InputRequest> questions,
        McpElicitationCoordinator? coordinator, CancellationToken ct)
    {
        var entry = task.Entry!;
        var responses = new Dictionary<string, InputResponse>(StringComparer.Ordinal);

        foreach (var (key, request) in questions)
        {
            if (request.Method != RequestMethods.ElicitationCreate || request.ElicitationParams is not { } elicitation)
            {
                // Sampling and roots aren't offered by this client; a task that needs one can't go on.
                McpTaskDiagnostics.InputRequest(serverName, "unsupported");
                task.Cancel($"the server asked the agent for {McpElicitationSchemaDescriber.Flatten(request.Method)}, which this client doesn't support");
                throw new OperationCanceledException(ct);
            }

            // With elicitation off for the server, there is no coordinator: the question is declined,
            // as the capability was never advertised for it.
            var answer = coordinator is null
                ? new ElicitResult { Action = McpElicitationActions.Decline }
                : await coordinator.HandleForCallAsync(elicitation, task.Scope!, ct);

            // A question cut short by the task's own cancellation (or the bridge stopping) isn't
            // answered: the task is ending, or the next start asks it again.
            ct.ThrowIfCancellationRequested();

            McpTaskDiagnostics.InputRequest(serverName, answer.Action);
            responses[key] = InputResponse.FromElicitResult(answer);
        }

        var failures = 0;
        var limit = Math.Max(1, _options.TaskPollFailureLimit);
        var delay = PollDelay(entry.PollIntervalMs);
        while (true)
        {
            using var lease = LeaseTaskServer(entry, out _);
            try
            {
                if (lease is null)
                    throw new InvalidOperationException($"MCP server \"{entry.Server}\" is not connected");

                await lease.Server.Client.UpdateTaskAsync(new UpdateTaskRequestParams
                {
                    TaskId = entry.ServerTaskId,
                    InputResponses = responses,
                }, ct);
                break;
            }
            catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
            {
                throw new McpException($"the server refused the answer ({ex.Message})", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (++failures >= limit)
                    throw;
                _logger.LogWarning(ex, "Sending answers to background task {TaskId} failed; retrying", task.TaskId);
                await Task.Delay(delay, _time, ct);
                delay = Backoff(delay);
            }
        }

        entry.ResolvedInputKeys.AddRange(responses.Keys);
        try
        {
            await _taskLedger.UpdateAsync(entry.TaskId, e => e.ResolvedInputKeys = [.. entry.ResolvedInputKeys], CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Only costs a repeat question after a restart.
            _logger.LogWarning(ex, "Could not record answered input requests of background task {TaskId}", task.TaskId);
        }
    }

    /// <summary>Hands a task's question to its session as a new turn (and to the chat as a form, where it can show one).</summary>
    private async Task DeliverTaskQuestionAsync(RunningMcpTask task, PendingQuestion question, CancellationToken ct)
    {
        var entry = task.Entry!;
        var formShown = await TryPublishFormAsync(question, ct);
        _logger.LogInformation("← MCP {Server}/{Tool} background task {TaskId} handed back question {QuestionId}",
            entry.Server, entry.Tool, entry.TaskId, question.QuestionId);

        await PublishTaskResultAsync(new McpTaskResult
        {
            SessionId = entry.SessionId,
            TaskId = entry.TaskId,
            ServerName = entry.Server,
            ToolName = entry.Tool,
            Kind = McpTaskResult.KindQuestion,
            Content = McpTaskText.Question(entry, question, formShown),
            QuestionId = question.QuestionId,
            FormShown = formShown,
        }, ct);
    }

    /// <summary>Maps a completed task's result as a synchronous call's would be, and delivers it.</summary>
    private async Task DeliverCompletedAsync(
        RunningMcpTask task, ConnectedServer server, CompletedTaskResult completed, CancellationToken ct)
    {
        var entry = task.Entry!;
        var result = completed.Result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))
            as CallToolResult ?? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "(the server sent no result)" }] };

        var elapsed = (long)(_time.GetUtcNow() - entry.StartedAt).TotalMilliseconds;
        var response = await MapResultAsync(
            result, server, entry.Tool, entry.TaskId, entry.Tool,
            task.RewriteResponse ? server.AttachmentGateway.Value : null,
            task.Arguments, task.SentArguments, task.InputSchema,
            task.Scope?.Records ?? [], elapsed, ct);

        await EndTaskAsync(task, McpTaskEntryStatus.Completed, null,
            content: string.IsNullOrEmpty(response.Content) ? "(no content)" : response.Content,
            isError: response.IsError);
    }

    /// <summary>
    /// Records how a task ended, closes any question it left open, delivers the ending to its
    /// session, and forgets it. Delivery is recorded only once it went out, so a restart in
    /// between delivers it again.
    /// </summary>
    private async Task EndTaskAsync(
        RunningMcpTask task, string status, string? reason, string? content = null, bool isError = true)
    {
        var entry = task.Entry!;
        entry.Status = status;
        entry.Reason = reason;
        entry.CancelledByAgent |= task.CancelledByAgent;
        entry.EndedAt ??= _time.GetUtcNow();

        try
        {
            await _taskLedger.UpdateAsync(entry.TaskId, e =>
            {
                e.Status = entry.Status;
                e.Reason = entry.Reason;
                e.CancelledByAgent = entry.CancelledByAgent;
                e.EndedAt = entry.EndedAt;
                e.PollIntervalMs = entry.PollIntervalMs;
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record how background task {TaskId} ended", entry.TaskId);
        }

        await _handback.CancelTaskAsync(entry.TaskId, "its background task ended");

        var completed = status == McpTaskEntryStatus.Completed;
        var delivered = await PublishTaskResultAsync(new McpTaskResult
        {
            SessionId = entry.SessionId,
            TaskId = entry.TaskId,
            ServerName = entry.Server,
            ToolName = entry.Tool,
            Kind = completed ? McpTaskResult.KindCompleted : McpTaskResult.KindEnded,
            Status = completed ? null : status,
            Content = completed ? content ?? "" : McpTaskText.Ended(entry),
            IsError = !completed || isError,
            NeedsReply = !(status == McpTaskEntryStatus.Cancelled && entry.CancelledByAgent),
        }, CancellationToken.None);

        if (delivered)
        {
            try
            {
                await _taskLedger.MarkDeliveredAsync(entry.TaskId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // At worst a restart delivers it again; the task id lets the session see it's a repeat.
                _logger.LogWarning(ex, "Could not record the delivery of background task {TaskId}", entry.TaskId);
            }
        }

        McpTaskDiagnostics.Record(entry.Server, status);
        _logger.LogInformation("Background task {TaskId} ({Server}/{Tool}) ended: {Status}{Reason}",
            entry.TaskId, entry.Server, entry.Tool, status, reason is null ? "" : $" ({reason})");

        _tasks.Remove(entry.TaskId);
        task.Cts?.Dispose();
    }

    /// <summary>
    /// Publishes something a task has for its session. Its content goes into the session's working
    /// memory first, so a subagent waiting on its tasks finds the result among its outputs.
    /// </summary>
    private async Task<bool> PublishTaskResultAsync(McpTaskResult message, CancellationToken ct)
    {
        try
        {
            if (_workingMemory is not null && message.Kind != McpTaskResult.KindQuestion)
            {
                var key = $"{message.SessionId}/mcp-task/{message.ServerName}/{message.TaskId}/result";
                await _workingMemory.SetAsync(key, message.Content, ttl: TaskResultTtl,
                    category: "mcp-task-result", tags: [message.ServerName, message.TaskId]);
                message = message with { WorkingMemoryKey = key };
            }

            await _publisher.PublishAsync($"{McpTaskResult.TopicPrefix}.{_agentName}",
                message.ToEnvelope<McpTaskResult>(source: _agentName), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not deliver background task {TaskId}'s {Kind} to {Session}",
                message.TaskId, message.Kind, message.SessionId);
            return false;
        }
    }

    /// <summary>
    /// Startup: every task the ledger holds whose ending hasn't reached its session is polled
    /// again (its server still has it, or says it doesn't), or — for an ending the bridge itself
    /// decided before the restart — has that ending delivered.
    /// </summary>
    private async Task ResumeTasksAsync(CancellationToken ct)
    {
        IReadOnlyList<McpTaskEntry> entries;
        try
        {
            entries = await _taskLedger.ReadAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read the MCP task ledger at {Path}", _taskLedger.Path);
            return;
        }

        foreach (var entry in entries.Where(e => !e.IsSettled))
        {
            var task = _tasks.TryReserve(entry.TaskId, entry.SessionId, 0, 0, force: true, out _)!;
            if (entry.Status == McpTaskEntryStatus.Running || McpTaskEntryStatus.IsServerReported(entry.Status))
            {
                entry.Status = McpTaskEntryStatus.Running;
                LaunchTask(task, entry, entry.Arguments, resumed: true);
                McpTaskDiagnostics.Record(entry.Server, "resumed");
                _logger.LogInformation("Resumed polling background task {TaskId} ({Server}/{Tool}) for {Session} after a restart",
                    entry.TaskId, entry.Server, entry.Tool, entry.SessionId);
            }
            else
            {
                task.Entry = entry;
                TrackParked(EndTaskAsync(task, entry.Status, entry.Reason));
            }
        }
    }

    /// <summary>
    /// Cancels every task of <paramref name="serverName"/>: the server was removed, or its entry no
    /// longer runs tasks or points elsewhere. The server is asked to stop each one now, while the
    /// connection that started it is still current; each task's loop then tells its session.
    /// </summary>
    private async Task CancelServerTasksAsync(string serverName, string reason)
    {
        foreach (var task in _tasks.All())
        {
            if (task.Entry is not { } entry || !string.Equals(entry.Server, serverName, StringComparison.OrdinalIgnoreCase))
                continue;

            task.CancelledOnServer = true;
            if (task.Cancel(reason))
                await TryCancelOnServerAsync(entry);
        }
    }

    /// <summary><c>mcp_cancel_task</c>: stops a task the asking session started.</summary>
    private async Task<McpCancelTaskResponse> CancelTaskByAgentAsync(McpCancelTaskRequest req, CancellationToken ct)
    {
        var task = string.IsNullOrEmpty(req.TaskId) ? null : _tasks.Get(req.TaskId);
        if (task?.Entry is null || !SameSession(task.SessionId, req.SessionId))
        {
            var ended = string.IsNullOrEmpty(req.TaskId) ? null : await _taskLedger.GetAsync(req.TaskId, ct);
            if (ended is not null && SameSession(ended.SessionId, req.SessionId))
            {
                return new McpCancelTaskResponse
                {
                    TaskId = req.TaskId,
                    Error = $"Task \"{ended.TaskId}\" has already ended ({ended.Status}); there is nothing to cancel.",
                };
            }

            var running = req.SessionId is null ? [] : _tasks.ForSession(req.SessionId).Where(t => t.Entry is not null).ToList();
            return new McpCancelTaskResponse
            {
                TaskId = req.TaskId ?? "",
                Error = $"There is no running background task with id \"{McpElicitationSchemaDescriber.Flatten(req.TaskId ?? "")}\" in this conversation." +
                        (running.Count == 0
                            ? ""
                            : " Running here: " + string.Join(", ", running.Select(t => $"\"{t.TaskId}\" ({t.Entry!.Server}/{t.Entry.Tool})")) + "."),
            };
        }

        var cancelling = task.Cancel("the agent cancelled it", byAgent: true);
        _logger.LogInformation("Background task {TaskId} ({Server}/{Tool}) {Outcome} by mcp_cancel_task",
            task.TaskId, task.Entry.Server, task.Entry.Tool, cancelling ? "cancelled" : "already being cancelled");

        return new McpCancelTaskResponse
        {
            TaskId = task.TaskId,
            Message = $"Task \"{task.TaskId}\" ({task.Entry.Server}/{task.Entry.Tool}) is being cancelled. " +
                      "The server has been asked to stop it; it won't produce a result.",
        };
    }

    /// <summary>
    /// A lease on the server a task runs on, found by name or, after a rename, by the entry's id.
    /// <paramref name="configured"/> says whether the server is still configured at all.
    /// </summary>
    private ServerLease? LeaseTaskServer(McpTaskEntry entry, out bool configured)
    {
        var name = entry.Server;
        if (!_connections.IsConfigured(name) && entry.ServerId is { Length: > 0 } id)
        {
            var renamed = _connections.ConfiguredNames.FirstOrDefault(n =>
                _connections.TryGetConfig(n, out var c) && string.Equals(c.Id, id, StringComparison.Ordinal));
            if (renamed is not null)
                name = renamed;
        }

        configured = _connections.IsConfigured(name);
        return configured ? _connections.Lease(name) : null;
    }

    /// <summary>Best effort: asks the task's server to stop it. Cancellation is cooperative anyway.</summary>
    private async Task TryCancelOnServerAsync(McpTaskEntry entry)
    {
        using var lease = LeaseTaskServer(entry, out _);
        if (lease is not null)
            await TryCancelOnServerAsync(lease.Server.Client, entry.ServerTaskId, lease.Server.Name);
    }

    private async Task TryCancelOnServerAsync(McpClient client, string serverTaskId, string serverName)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TaskCancelTimeout);
            await client.CancelTaskAsync(serverTaskId, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask MCP {Server} to cancel task {ServerTaskId}", serverName, serverTaskId);
        }
    }

    /// <summary>The user turn the task was serving, for its ledger entry; only for a user conversation.</summary>
    private async Task<PendingTrigger?> FindTaskTriggerAsync(string sessionId, DateTimeOffset before, CancellationToken ct)
    {
        if (_conversationMemory is null || !HandbackSessions.IsUserSession(sessionId))
            return null;

        try
        {
            return await FindTriggerAsync(sessionId, before, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read session {Session}'s conversation for a task's context", sessionId);
            return null;
        }
    }

    private TimeSpan PollDelay(long? suggestedMs)
    {
        var min = Math.Max(10, _options.TaskPollMinMs);
        var max = Math.Max(min, _options.TaskPollMaxMs);
        return TimeSpan.FromMilliseconds(Math.Clamp(suggestedMs ?? 1000, min, max));
    }

    private TimeSpan Backoff(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(Math.Min(Math.Max(1, delay.TotalMilliseconds) * 2, Math.Max(_options.TaskPollMinMs, _options.TaskPollMaxMs)));

    private static string DescribeTaskError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "an unspecified error";
        }

        return error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "an unspecified error" : error.GetRawText();
    }

    private static IDictionary<string, JsonElement>? ToJsonArguments(Dictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
            return null;

        var typeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object));
        return arguments.ToDictionary(
            a => a.Key,
            a => a.Value is JsonElement element ? element : JsonSerializer.SerializeToElement(a.Value, typeInfo));
    }

    private static string NewTaskId() => "t_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private static string? FlattenOrNull(string? text) =>
        text is null ? null : McpElicitationSchemaDescriber.Flatten(text);

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..(max - 1)] + "…";
}
