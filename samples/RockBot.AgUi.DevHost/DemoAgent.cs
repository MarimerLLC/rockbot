using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.AgUi.DevHost;

/// <summary>
/// Stands in for the agent on the message bus: no LLM, a script. It answers a message the way
/// the real agent does on the wire (a run that starts, progress, a tool call, a task list, a
/// reply, the run's end), and certain words in the message make it do the other things a client
/// has to handle:
/// <list type="bullet">
/// <item><c>question</c>: an MCP question, as a form. Answering or declining it gets a reply.</item>
/// <item><c>notify</c>: output nobody asked for, a moment later: a subagent that starts, works
/// and hands in a result, and a scheduled task's report with its origin.</item>
/// <item><c>image</c>: a reply with an image attachment.</item>
/// <item><c>slow</c>: a turn that runs (with heartbeats) until it is cancelled, or for a minute.</item>
/// <item><c>silent</c>: a turn the agent never starts, so the proxy stops waiting for it.</item>
/// </list>
/// It also keeps the conversation, stores uploads and answers the agent-info requests.
/// </summary>
public sealed class DemoAgent : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IMessagePublisher _publisher;
    private readonly string _attachments;
    private readonly List<ISubscription> _subscriptions = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<ConversationHistoryTurn>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _turns = new(StringComparer.Ordinal);
    private readonly List<McpQuestionAnswer> _answers = [];
    private readonly List<UserMessage> _messages = [];
    private readonly CancellationTokenSource _stopping = new();

    private DemoAgent(IMessagePublisher publisher, string attachmentsPath)
    {
        _publisher = publisher;
        _attachments = attachmentsPath;
    }

    /// <summary>How long the agent waits before sending what the word <c>notify</c> asks for.</summary>
    public TimeSpan NotifyDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The messages the agent received, in order.</summary>
    public IReadOnlyList<UserMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    /// <summary>The form answers the agent received, in order.</summary>
    public IReadOnlyList<McpQuestionAnswer> Answers
    {
        get { lock (_lock) return [.. _answers]; }
    }

    public static async Task<DemoAgent> StartAsync(IMessagePublisher publisher, IMessageSubscriber subscriber, string attachmentsPath)
    {
        var agent = new DemoAgent(publisher, attachmentsPath);

        await agent.SubscribeAsync<UserMessage>(subscriber, UserProxyTopics.UserMessage, agent.OnMessageAsync);
        await agent.SubscribeAsync<CancelSessionRequest>(subscriber, UserProxyTopics.CancelSession, agent.OnCancelAsync);
        await agent.SubscribeAsync<ClearContextRequest>(subscriber, UserProxyTopics.ClearContext, agent.OnClearAsync);
        await agent.SubscribeAsync<McpQuestionAnswer>(subscriber, UserProxyTopics.McpAnswer, agent.OnAnswerAsync);

        await agent.AnswerAsync<ConversationHistoryRequest, ConversationHistoryResponse>(
            subscriber, UserProxyTopics.ConversationHistoryRequest, r => new ConversationHistoryResponse { Turns = agent.History(r.SessionId) });
        await agent.AnswerAsync<AgentInfoRequest, AgentInfoResponse>(
            subscriber, UserProxyTopics.AgentInfoRequest, _ => new AgentInfoResponse { AgentName = DevProxy.AgentName, AgentVersion = "demo" });
        await agent.AnswerAsync<ActiveStatusRequest, ActiveStatusResponse>(
            subscriber, UserProxyTopics.ActiveStatusRequest, r => new ActiveStatusResponse
            {
                Subagents = [], IsProcessing = r.SessionId is { } session && agent.IsRunning(session), Work = [],
            });
        await agent.AnswerAsync<AttachmentUploadRequest, AttachmentUploadResponse>(
            subscriber, UserProxyTopics.AttachmentUploadRequest, agent.Store);

        return agent;
    }

    // ── A turn ───────────────────────────────────────────────────────────────

    private async Task OnMessageAsync(MessageEnvelope envelope, UserMessage message)
    {
        lock (_lock) _messages.Add(message);
        var text = message.Content;
        if (Says(text, "silent"))
            return;

        var session = message.SessionId;
        var runId = envelope.CorrelationId ?? Guid.NewGuid().ToString("N");
        var turn = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        lock (_lock) _turns[session] = turn;
        Remember(session, "user", text, message.Attachments);

        Task Send<T>(T payload) => _publisher.PublishAsync(envelope.ReplyTo!,
            payload.ToEnvelope<T>(source: DevProxy.AgentName, correlationId: envelope.CorrelationId));
        Task Reply(string content, bool isFinal = true, ToolCallInfo? toolCall = null, IReadOnlyList<AgentAttachment>? attachments = null) =>
            Send(new AgentReply
            {
                Content = content, SessionId = session, AgentName = DevProxy.AgentName, IsFinal = isFinal,
                ToolCall = toolCall, Attachments = attachments, Source = ReplySource.Primary,
            });
        Task Finish(string outcome) => Send(new RunFinished
        {
            ThreadId = session, RunId = runId, Outcome = outcome, Timestamp = DateTimeOffset.UtcNow,
        });

        await Send(new RunStarted
        {
            ThreadId = session, RunId = runId, AgentName = DevProxy.AgentName, HeartbeatIntervalSeconds = 10,
            Timestamp = DateTimeOffset.UtcNow,
        });

        try
        {
            string reply;
            IReadOnlyList<AgentAttachment>? attachments = null;

            if (Says(text, "slow"))
            {
                // Runs until someone cancels: heartbeats keep the proxy waiting.
                for (var seconds = 1; seconds <= 60; seconds++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), turn.Token);
                    await Send(new RunHeartbeat
                    {
                        ThreadId = session, RunId = runId, Timestamp = DateTimeOffset.UtcNow,
                        ElapsedSeconds = seconds, Status = $"Still thinking ({seconds}s)…",
                    });
                }
                reply = "That took a while.";
            }
            else if (Says(text, "question"))
            {
                // The bridge broadcasts a question; it is not part of the turn's own traffic.
                await FormAsync(session);
                await Task.Delay(TimeSpan.FromMilliseconds(250), turn.Token);
                reply = "The scheduler needs a time zone; please fill in the form.";
            }
            else if (Says(text, "image"))
            {
                var name = $"demo-{Guid.NewGuid():N}.png";
                await File.WriteAllBytesAsync(Path.Combine(_attachments, name), DemoImage(), turn.Token);
                attachments = [new AgentAttachment { Mime = "image/png", Path = name, FileName = "demo.png" }];
                reply = "Here is an image.";
            }
            else
            {
                var search = new ToolCallInfo
                {
                    ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8], ToolCallName = "web_search",
                    Phase = ToolCallPhase.Started, ArgsSummary = $"query: {Truncate(text, 40)}",
                };
                TaskListItem[] plan =
                [
                    new() { Id = "1", Description = "Look it up", Status = "in_progress" },
                    new() { Id = "2", Description = "Write the answer", Status = "pending" },
                ];

                await Reply("Thinking…", isFinal: false);
                await Send(new TaskListSnapshot { ThreadId = session, RunId = runId, Source = ReplySource.Primary, Items = plan });
                await Reply("Calling web_search…", isFinal: false, toolCall: search);
                await Task.Delay(TimeSpan.FromMilliseconds(150), turn.Token);
                await Reply("web_search finished", isFinal: false,
                    toolCall: search with { Phase = ToolCallPhase.Finished, Result = "3 results", DurationMs = 150 });
                await Send(new TaskListDelta
                {
                    ThreadId = session, RunId = runId, Source = ReplySource.Primary, Item = plan[0] with { Status = "completed" },
                });
                await Send(new TaskListDelta
                {
                    ThreadId = session, RunId = runId, Source = ReplySource.Primary, Item = plan[1] with { Status = "completed" },
                });

                reply = message.Attachments is { Count: > 0 } files
                    ? $"Echo: {text}\n\nI got {files.Count} file(s): {string.Join(", ", files.Select(f => f.FileName ?? f.Path))}."
                    : $"Echo: {text}";

                if (Says(text, "notify"))
                {
                    reply += "\n\nI'll report back in a moment.";
                    _ = Task.Run(() => NotifyLaterAsync(session, text));
                }
            }

            Remember(session, "assistant", reply, attachments);
            await Reply(reply, attachments: attachments);
            await Finish(RunOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            await Finish(RunOutcome.Cancelled);
        }
        finally
        {
            lock (_lock)
            {
                if (_turns.TryGetValue(session, out var current) && current == turn)
                    _turns.Remove(session);
            }
            turn.Dispose();
        }
    }

    private Task OnCancelAsync(MessageEnvelope envelope, CancelSessionRequest request)
    {
        lock (_lock)
        {
            if (_turns.TryGetValue(request.SessionId, out var turn))
                turn.Cancel();
        }
        return Task.CompletedTask;
    }

    private async Task OnClearAsync(MessageEnvelope envelope, ClearContextRequest request)
    {
        lock (_lock) _history.Remove(request.SessionId);
        await OnCancelAsync(envelope, new CancelSessionRequest { SessionId = request.SessionId });
        await BroadcastAsync(new AgentReply
        {
            Content = "Context cleared — starting fresh.", SessionId = request.SessionId, AgentName = DevProxy.AgentName,
            Source = ReplySource.Primary,
        });
    }

    // ── Output nobody asked for ──────────────────────────────────────────────

    /// <summary>A subagent's whole life, then a scheduled task's report: everything arrives on the notify stream.</summary>
    public async Task NotifyLaterAsync(string session, string prompt)
    {
        try
        {
            await Task.Delay(NotifyDelay, _stopping.Token);
            var taskId = "t" + Guid.NewGuid().ToString("N")[..6];
            var source = ReplySource.Subagent(taskId);
            var started = DateTimeOffset.UtcNow;

            await EventAsync(new SubagentStarted
            {
                SubagentRunId = taskId, Kind = ReplySourceKind.Subagent, Description = "Research the vendors",
                ThreadId = session, StartedAt = started,
            });
            await BroadcastAsync(new AgentReply
            {
                Content = "Reading the price list", SessionId = session, AgentName = $"subagent-{taskId}", IsFinal = false, Source = source,
            });
            await EventAsync(new TaskListSnapshot
            {
                ThreadId = session, Source = source,
                Items = [new TaskListItem { Id = "1", Description = "Compare prices", Status = "in_progress" }],
            });
            await Task.Delay(TimeSpan.FromMilliseconds(300), _stopping.Token);
            await BroadcastAsync(new AgentReply
            {
                Content = "Vendor B is the cheapest by 12%.", SessionId = session, AgentName = $"subagent-{taskId}",
                IsFinal = false, IsCompletion = true, Source = source,
                Origin = new ReplyOrigin("agui", Truncate(prompt, 60), started, session),
            });
            await EventAsync(new SubagentFinished
            {
                SubagentRunId = taskId, Kind = ReplySourceKind.Subagent, ThreadId = session,
                Outcome = SubagentOutcome.Success, FinishedAt = DateTimeOffset.UtcNow,
            });

            // A scheduled task replies in its own session, which every thread hears.
            await BroadcastAsync(new AgentReply
            {
                Content = "**Daily summary.** Nothing needs your attention today.", SessionId = "scheduled",
                AgentName = DevProxy.AgentName, Source = ReplySource.Scheduled,
                Origin = new ReplyOrigin("scheduler", "Daily summary", started.AddMinutes(-5), "scheduled"),
            });
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // ── MCP questions ────────────────────────────────────────────────────────

    /// <summary>Asks a question as the MCP bridge does: a broadcast reply that carries the form.</summary>
    public Task FormAsync(string session, McpQuestionForm? form = null)
    {
        form ??= new McpQuestionForm
        {
            QuestionId = "q-" + Guid.NewGuid().ToString("N")[..8],
            ServerName = "scheduler",
            ToolName = "create_event",
            Message = "Which time zone should the meeting be in?",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Fields =
            [
                new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true },
                new McpQuestionField
                {
                    Name = "length", Kind = McpQuestionFieldKind.SingleSelect, Title = "Length",
                    Options = [new McpQuestionOption("30", "30 minutes"), new McpQuestionOption("60", "One hour")],
                    Default = "\"30\"",
                },
                new McpQuestionField { Name = "notify", Kind = McpQuestionFieldKind.Boolean, Title = "Email the attendees" },
            ],
        };

        return BroadcastAsync(new AgentReply
        {
            Content = $"{form.ServerName} asks: {form.Message}",
            SessionId = session,
            AgentName = "mcp-bridge",
            Source = ReplySource.Bridge,
            ContentType = McpQuestionForm.ContentType,
            StructuredData = JsonSerializer.Serialize(form, Json),
        });
    }

    private async Task OnAnswerAsync(MessageEnvelope envelope, McpQuestionAnswer answer)
    {
        lock (_lock) _answers.Add(answer);

        await BroadcastAsync(new McpQuestionStatus
        {
            SessionId = answer.SessionId, QuestionId = answer.QuestionId, AnsweredBy = "user",
            State = answer.Decline ? McpQuestionState.Declined : McpQuestionState.Answered,
        });

        var said = answer.Decline
            ? "Okay, I won't book it."
            : $"Booked. Your answer was `{answer.Answers}`.";
        Remember(answer.SessionId, "assistant", said, null);
        await BroadcastAsync(new AgentReply
        {
            Content = said, SessionId = answer.SessionId, AgentName = DevProxy.AgentName,
            Source = ReplySource.Primary, FollowsQuestionId = answer.QuestionId,
        });
    }

    // ── Uploads, history ─────────────────────────────────────────────────────

    private AttachmentUploadResponse Store(AttachmentUploadRequest request)
    {
        // The real agent takes images and PDF, and picks the stored name.
        if (!request.Mime.StartsWith("image/", StringComparison.Ordinal) && request.Mime != "application/pdf"
            || request.Mime == AttachmentMimeTypes.Svg)
            return new AttachmentUploadResponse { Success = false, Error = $"The agent doesn't take {request.Mime} files." };

        var name = Path.GetFileNameWithoutExtension(request.FileName) + "-" + Guid.NewGuid().ToString("N")[..4]
            + Path.GetExtension(request.FileName).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(_attachments, name), request.Data);
        return new AttachmentUploadResponse
        {
            Success = true,
            Attachment = new AgentAttachment { Mime = request.Mime, Path = name, FileName = request.FileName },
        };
    }

    private void Remember(string session, string role, string content, IReadOnlyList<AgentAttachment>? attachments)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(session, out var turns))
                _history[session] = turns = [];
            turns.Add(new ConversationHistoryTurn
            {
                Role = role, Content = content, Timestamp = DateTimeOffset.UtcNow, Attachments = attachments,
                AgentName = role == "assistant" ? DevProxy.AgentName : null,
            });
        }
    }

    private IReadOnlyList<ConversationHistoryTurn> History(string session)
    {
        lock (_lock) return _history.TryGetValue(session, out var turns) ? [.. turns] : [];
    }

    private bool IsRunning(string session)
    {
        lock (_lock) return _turns.ContainsKey(session);
    }

    // ── The bus ──────────────────────────────────────────────────────────────

    /// <summary>Publishes on the reply topic every proxy hears, uncorrelated: unsolicited replies, statuses.</summary>
    public Task BroadcastAsync<T>(T payload) =>
        _publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{DevProxy.AgentName}", payload.ToEnvelope<T>(source: DevProxy.AgentName));

    /// <summary>Publishes on the event topic: subagent lifecycle and their task lists.</summary>
    public Task EventAsync<T>(T payload) =>
        _publisher.PublishAsync($"{UserProxyTopics.UserEvent}.{DevProxy.AgentName}", payload.ToEnvelope<T>(source: DevProxy.AgentName));

    private async Task SubscribeAsync<T>(IMessageSubscriber subscriber, string topic, Func<MessageEnvelope, T, Task> handle) where T : class
    {
        _subscriptions.Add(await subscriber.SubscribeAsync(
            $"{topic}.{DevProxy.AgentName}", $"demo-agent.{topic}",
            (envelope, ct) =>
            {
                // A turn can take a while (it may wait to be cancelled), and the cancel arrives on
                // the same bus: never hold the bus's thread for it.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handle(envelope, envelope.GetPayload<T>()!);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"demo agent: {topic}: {ex.Message}");
                    }
                });
                return Task.FromResult(MessageResult.Ack);
            }));
    }

    private async Task AnswerAsync<TRequest, TResponse>(IMessageSubscriber subscriber, string topic, Func<TRequest, TResponse> answer)
        where TRequest : class where TResponse : class
    {
        _subscriptions.Add(await subscriber.SubscribeAsync(
            $"{topic}.{DevProxy.AgentName}", $"demo-agent.{topic}",
            async (envelope, ct) =>
            {
                if (envelope.ReplyTo is { } replyTo)
                {
                    var response = answer(envelope.GetPayload<TRequest>()!);
                    await _publisher.PublishAsync(replyTo,
                        response.ToEnvelope<TResponse>(source: DevProxy.AgentName, correlationId: envelope.CorrelationId), ct);
                }
                return MessageResult.Ack;
            }));
    }

    private static bool Says(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    /// <summary>A small PNG (a colour gradient), made here so the sample ships no binary file.</summary>
    public static byte[] DemoImage(int size = 96)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            raw.WriteByte(0); // no filter
            for (var x = 0; x < size; x++)
            {
                raw.WriteByte((byte)(255 * x / size));
                raw.WriteByte((byte)(255 * y / size));
                raw.WriteByte(160);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.GetBuffer(), 0, (int)raw.Length);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;  // bit depth
        header[9] = 2;  // RGB

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);

        var crc = 0xFFFFFFFFu;
        foreach (var b in (ReadOnlySpan<byte>)[.. type, .. data])
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        png.Write(number);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var subscription in _subscriptions)
            await subscription.DisposeAsync();
    }
}
