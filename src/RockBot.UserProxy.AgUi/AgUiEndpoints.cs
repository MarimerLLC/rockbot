using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Features;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// The run ids with an open stream on this proxy. A run id is also the turn's correlation id on
/// the bus, where it must be unique while the turn is pending, so a second request with the same
/// id is refused (HTTP 409) before it starts a stream.
/// </summary>
internal sealed class AgUiRunRegistry
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public bool TryStart(string runId) => _active.TryAdd(runId, 0);

    public void Finish(string runId) => _active.TryRemove(runId, out _);
}

/// <summary>
/// The proxy's HTTP surface (#794): runs, the notify stream, cancel, history and health here; the
/// plain REST endpoints in <see cref="AgUiRestEndpoints"/>.
/// </summary>
internal static class AgUiEndpoints
{
    /// <summary>How long a cancel request sent after the client went away may take.</summary>
    private static readonly TimeSpan AbortCancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The <c>RUN_ERROR</c> code for a run cut short because this proxy is shutting down.</summary>
    public const string ProxyStoppingCode = "proxy_stopping";

    public static IEndpointRouteBuilder MapAgUi(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<AgUiOptions>();

        // Everything a client calls is in this one group, so app auth is one line: in app mode
        // every endpoint in it needs a caller on the allowlist (a bearer token, or for the two
        // requests a browser makes by itself, an access ticket). In network mode nothing is
        // required and whoever can reach the proxy is allowed in.
        var api = app.MapGroup("");
        if (options.Auth.Enabled)
            api.RequireAuthorization();
        // In both modes, a request to any of them that names an Origin must name an allowed one.
        api.WithMetadata(new OriginCheckedMetadata());
        api.MapPost("/agui", RunAsync);
        api.MapGet("/agui/notify", NotifyAsync);
        api.MapPost("/threads/{id}/cancel", CancelAsync);
        api.MapGet("/threads/{id}/messages", MessagesAsync);
        api.MapPost("/questions/{id}/answer", AnswerQuestionAsync);
        api.MapAgUiRest();

        // The two endpoints that stay anonymous whatever the group requires: a browser sent back
        // by an authorization server has no token to show, and neither has a probe.
        app.MapMcpSignInCallback();

        // For probes: ready only while the proxy holds its subscriptions on the bus.
        app.MapGet("/healthz", (UserProxyService proxy) =>
                proxy.IsConnected
                    ? Results.Text("ok")
                    : Results.Text("not connected to the message bus", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// <c>POST /agui</c>: one AG-UI run. Everything that can be refused from the request alone is
    /// refused with a plain HTTP error before the first byte of the stream; after that, problems
    /// end the stream with <c>RUN_ERROR</c>.
    /// </summary>
    private static async Task RunAsync(
        HttpContext context,
        UserProxyService proxy,
        UserProxyOptions proxyOptions,
        AgUiOptions options,
        AgUiRunRegistry runs,
        McpQuestionRegistry questions,
        AttachmentPathResolver attachments,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("RockBot.UserProxy.AgUi.Runs");

        RunAgentInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync(
                context.Request.Body, AGUIJsonSerializerContext.Default.RunAgentInput, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or BadHttpRequestException or IOException))
        {
            // The SDK's converters report a malformed document in more than one way.
            await RefuseAsync(context, StatusCodes.Status400BadRequest, $"The body is not a valid RunAgentInput: {ex.Message}");
            return;
        }

        if (input is null)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "The body is not a valid RunAgentInput.");
            return;
        }

        var userId = AgUiIdentity.UserId(context.User, options);
        var request = AgUiRequestMapper.Map(input, options, proxyOptions.ResolveChannelName(), userId);
        if (request.IsRefused)
        {
            await RefuseAsync(context, request.ErrorStatus, request.Error ?? "The request was refused.");
            return;
        }

        // An attachment reference has the right form (the mapper saw to that); this is whether
        // there is such a file, which only the volume can say.
        if (AgUiRestEndpoints.FirstMissing(attachments, request.Message?.Attachments) is { } missing)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest,
                $"There is no attachment '{missing.Path}'. Upload the file with POST /attachments and use the path it returns.");
            return;
        }

        if (!proxy.IsConnected)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "The proxy is not connected to the message bus.");
            return;
        }

        if (!runs.TryStart(input.RunId))
        {
            await RefuseAsync(context, StatusCodes.Status409Conflict, $"Run '{input.RunId}' is already in progress.");
            return;
        }

        try
        {
            // The emitter is called from the bus's message-handling thread; the channel moves its
            // events to this request, which alone writes the response.
            var events = Channel.CreateUnbounded<BaseEvent>(new UnboundedChannelOptions { SingleReader = true });
            var emitter = new TurnEventEmitter(
                input.ThreadId, input.RunId, e => events.Writer.TryWrite(e), attachmentUrl: AttachmentLinks.Url);
            var stopping = lifetime.ApplicationStopping;

            if (request.Resume is { } resume)
            {
                var start = questions.TryBeginResume(input.ThreadId, [.. resume.Select(r => r.QuestionId)], emitter);
                if (start.BusyQuestionId is { } busy)
                {
                    await RefuseAsync(context, StatusCodes.Status409Conflict,
                        $"Another run is already resuming interrupt '{busy}'.");
                }
                else if (start.Run is not { } run)
                {
                    await StreamRunAsync(context, options, events, logger, input.RunId, stopping, (_, _) =>
                        RefuseInStream(emitter, events.Writer, start.AllOpen, TurnEventMapper.ErrorCodes.ResumeIncomplete,
                            "resume must answer every open interrupt. Not answered: "
                            + string.Join(", ", start.Unanswered.Select(q => q.Form.QuestionId)) + "."));
                }
                else
                {
                    var resuming = new ResumeRun(proxy, questions, run, emitter, events.Writer, logger,
                        userId, proxyOptions.DefaultReplyTimeout, options.ResumeReplyTimeout);
                    await StreamRunAsync(context, options, events, logger, input.RunId, stopping,
                        (stop, clientGone) => resuming.RunAsync(resume, stop, clientGone));
                }
                return;
            }

            var message = request.Message!;
            if (questions.TryBeginRun(input.ThreadId, request.QuestionMode, emitter, out var blockedBy) is not { } registration)
            {
                // A state the client is expected to handle, not a malformed request, so it is a
                // run that fails rather than an HTTP error: an AG-UI client shows RUN_ERROR to its
                // user, where it may treat an HTTP failure as a transport problem and retry. The
                // questions go first, so a client that lost them (a reload) can still answer.
                await StreamRunAsync(context, options, events, logger, input.RunId, stopping, (_, _) =>
                    RefuseInStream(emitter, events.Writer, blockedBy, TurnEventMapper.ErrorCodes.InterruptsPending,
                        "This thread has interrupts to answer before it takes new input. Send a run whose resume answers "
                        + string.Join(", ", blockedBy.Select(q => q.Form.QuestionId)) + "."));
                return;
            }

            await StreamRunAsync(context, options, events, logger, input.RunId, stopping, (stop, clientGone) =>
                RunTurnAsync(proxy, questions, registration, message, emitter, events.Writer, logger, stop, clientGone));
        }
        finally
        {
            runs.Finish(input.RunId);
        }
    }

    /// <summary>
    /// Serves a run's events as the response, while <paramref name="body"/> produces them. The
    /// body gets two tokens: the first is cancelled when it should stop for any reason, the second
    /// only when the client went away. It completes the channel when it is done.
    /// </summary>
    private static async Task StreamRunAsync(
        HttpContext context, AgUiOptions options, Channel<BaseEvent> events, ILogger logger, string runId,
        CancellationToken stopping, Func<CancellationToken, CancellationToken, Task> body)
    {
        var response = StartEventStream(context);

        // Two reasons to stop waiting for the agent, handled differently: the client went away, or
        // this proxy is shutting down (end the stream and leave the agent to finish its work).
        using var clientGone = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(clientGone.Token, stopping);
        var work = body(stop.Token, clientGone.Token);

        try
        {
            await PumpAsync(events.Reader, response, options.KeepAliveInterval,
                e => $"data: {AgUiJson.Serialize(e)}\n\n", clientGone.Token);
        }
        catch (OperationCanceledException)
        {
            // The client went away; the body deals with it.
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Writing run {RunId} failed; treating it as a client abort", runId);
        }
        finally
        {
            // A stream that can't be written is an abort too. After a normal end this does nothing.
            await clientGone.CancelAsync();
        }

        await work;
    }

    /// <summary>
    /// A run refused for the state of its thread's interrupts: <c>RUN_STARTED</c>, the open
    /// questions (each as <c>rockbot.mcp_question</c>, with its interrupt), then <c>RUN_ERROR</c>.
    /// </summary>
    private static Task RefuseInStream(
        TurnEventEmitter emitter, ChannelWriter<BaseEvent> events, IReadOnlyList<OpenQuestion> open, string code, string message)
    {
        emitter.Start();
        emitter.Emit(m => open.Select(q => McpQuestionInterrupts.QuestionEvent(m, q.Reply, q.Form)));
        emitter.Fail(code, message);
        events.TryComplete();
        return Task.CompletedTask;
    }

    /// <summary>Sends the turn and feeds its outcome to the emitter. Never throws; always completes the channel.</summary>
    private static async Task RunTurnAsync(
        UserProxyService proxy,
        McpQuestionRegistry questions,
        RunQuestions registration,
        UserMessage message,
        TurnEventEmitter emitter,
        ChannelWriter<BaseEvent> events,
        ILogger logger,
        CancellationToken stop,
        CancellationToken clientGone)
    {
        try
        {
            emitter.Start();
            var result = await proxy.SendTurnAsync(
                message,
                new TurnOptions { CorrelationId = emitter.RunId, Progress = emitter, Observer = emitter },
                stop);

            logger.LogDebug("Run {RunId} on thread {ThreadId} ended as {Outcome}", emitter.RunId, emitter.ThreadId, result.Outcome);

            // The run is taken out of the registry before its last event is written, so a form
            // that arrives from here on goes to the notify stream instead of a closed stream. A
            // run that ends with RUN_FINISHED carries the open questions as interrupts (in
            // interrupt mode); one that fails or is cancelled can't, so they go to notify.
            var carries = result.Outcome is TurnResultOutcome.Success or TurnResultOutcome.TimedOut;
            var interrupts = Interrupts(questions.EndRun(registration, carryInterrupts: carries));

            // Timed out: this proxy stopped waiting, but the agent may still be working. Its
            // reply then arrives uncorrelated and goes to the notify stream, so the run ends
            // normally and says so. Every other outcome is final.
            if (result.Outcome == TurnResultOutcome.TimedOut)
                emitter.FinishReplyPending(result, interrupts);
            else
                emitter.Finish(result, interrupts);
        }
        catch (OperationCanceledException) when (clientGone.IsCancellationRequested)
        {
            // AG-UI's way to cancel is to abort the request. Nobody is left to read the agent's
            // acknowledgement, so nothing more is emitted.
            questions.EndRun(registration, carryInterrupts: false);
            logger.LogInformation("Client aborted run {RunId}; cancelling session {ThreadId}", emitter.RunId, emitter.ThreadId);
            try
            {
                using var timeout = new CancellationTokenSource(AbortCancelTimeout);
                await proxy.CancelSessionAsync(message.SessionId, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to cancel session {ThreadId} after the client aborted run {RunId}",
                    emitter.ThreadId, emitter.RunId);
            }
        }
        catch (OperationCanceledException)
        {
            // This proxy is stopping (a rollout, say). The agent carries on with the turn; the
            // client is told so it can reconnect and read the reply from the thread's history.
            questions.EndRun(registration, carryInterrupts: false);
            logger.LogInformation("Ending run {RunId} on thread {ThreadId}: the proxy is stopping", emitter.RunId, emitter.ThreadId);
            emitter.Fail(ProxyStoppingCode, "The proxy is shutting down. The agent may still finish this turn.");
        }
        catch (Exception ex)
        {
            questions.EndRun(registration, carryInterrupts: false);
            logger.LogError(ex, "Run {RunId} on thread {ThreadId} failed", emitter.RunId, emitter.ThreadId);
            emitter.Fail(TurnEventMapper.ErrorCodes.ClientError, ex.Message);
        }
        finally
        {
            questions.EndRun(registration, carryInterrupts: false);
            events.TryComplete();
        }
    }

    internal static IReadOnlyList<AGUIInterrupt> Interrupts(IReadOnlyList<OpenQuestion> open) =>
        [.. open.Select(q => McpQuestionInterrupts.ToInterrupt(q.Form))];

    /// <summary>
    /// <c>GET /agui/notify?threadId=</c>: the thread's unsolicited output as server-sent events,
    /// each an AG-UI event with an <c>id</c>. Not a run: there is no <c>RUN_STARTED</c>, and the
    /// stream ends only when the client goes away, the proxy stops, or the client falls so far
    /// behind that it is dropped. A client that sends the last id it saw (<c>Last-Event-ID</c>, or
    /// <c>lastEventId</c> in the query for one that can't set headers) gets what it missed first.
    /// </summary>
    private static async Task NotifyAsync(
        HttpContext context,
        NotifyHub hub,
        AgUiOptions options,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("RockBot.UserProxy.AgUi.Notify");

        var threadId = context.Request.Query["threadId"].ToString();
        if (string.IsNullOrEmpty(threadId))
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "The threadId query parameter is required.");
            return;
        }

        if (!options.IsThreadAllowed(threadId))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, $"Thread '{threadId}' is not allowed.");
            return;
        }

        // A ticket is for one thread's stream: the one this request is about to open.
        if (AccessTicketHandler.GrantOf(context) is { } grant && !grant.CoversNotify(threadId))
        {
            await AgUiAuth.ChallengeAsync(context, "invalid_token", "The access ticket is not for this resource.");
            return;
        }

        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(lastEventId))
            lastEventId = context.Request.Query["lastEventId"].ToString();

        using var subscription = hub.Subscribe(threadId, lastEventId);
        if (subscription is null)
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, $"Thread '{threadId}' is not allowed.");
            return;
        }

        // App mode: the stream ends when the caller's token does (for a ticket, the token it was
        // issued for), so a stream is never open longer than its credential is good. The client
        // reconnects with a fresh token and Last-Event-ID, and misses nothing. A run is different:
        // it is bounded (UserProxy:MaxTurnDuration) and cutting it would cancel the turn, so a
        // run that was authorized when it started is allowed to finish.
        using var expired = new CancellationTokenSource();
        if (AgUiIdentity.Expires(context.User) is { } expires)
        {
            var left = expires + options.Auth.ClockSkew - (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            if (left <= TimeSpan.Zero)
                expired.Cancel();
            else if (left < TimeSpan.FromDays(30))
                expired.CancelAfter(left);
        }

        var response = StartEventStream(context);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping, subscription.Dropped, expired.Token);
        try
        {
            await PumpAsync(subscription.Reader, response, options.KeepAliveInterval,
                frame => $"id: {frame.Id}\ndata: {frame.Json}\n\n", stop.Token);
        }
        catch (OperationCanceledException)
        {
            // The client went away, the proxy is stopping, or the subscriber was dropped.
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Writing the notify stream of thread {ThreadId} failed", threadId);
        }

        if (subscription.IsDropped)
            logger.LogInformation("Ended a notify stream of thread {ThreadId}: the client fell too far behind", threadId);
        else if (expired.IsCancellationRequested)
            logger.LogDebug("Ended a notify stream of thread {ThreadId}: the caller's token expired", threadId);
    }

    /// <summary>Sets the response up as an unbuffered event stream.</summary>
    private static HttpResponse StartEventStream(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tells nginx (and proxies that follow its convention) not to buffer the stream.
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        return response;
    }

    /// <summary>
    /// Writes each item as the server-sent event <paramref name="format"/> makes of it, until the
    /// channel completes, with an SSE comment whenever nothing was written for
    /// <paramref name="keepAlive"/>.
    /// </summary>
    private static async Task PumpAsync<T>(
        ChannelReader<T> events, HttpResponse response, TimeSpan keepAlive, Func<T, string> format, CancellationToken ct)
    {
        await response.StartAsync(ct);

        while (true)
        {
            bool more;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (keepAlive > TimeSpan.Zero)
                    idle.CancelAfter(keepAlive);
                try
                {
                    more = await events.WaitToReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.WriteAsync(": keepalive\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    continue;
                }
            }

            if (!more)
                return;

            while (events.TryRead(out var e))
                await response.WriteAsync(format(e), ct);
            await response.Body.FlushAsync(ct);
        }
    }

    /// <summary><c>POST /threads/{id}/cancel</c>: stops the thread's in-flight work. An open run ends when the agent reports it cancelled.</summary>
    private static async Task<IResult> CancelAsync(
        string id, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!options.IsThreadAllowed(id))
            return Refusal(StatusCodes.Status403Forbidden, $"Thread '{id}' is not allowed.");

        await proxy.CancelSessionAsync(id, ct);
        return Results.Accepted();
    }

    /// <summary><c>GET /threads/{id}/messages</c>: the thread's history as a <c>MESSAGES_SNAPSHOT</c> event.</summary>
    private static async Task<IResult> MessagesAsync(
        string id, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!options.IsThreadAllowed(id))
            return Refusal(StatusCodes.Status403Forbidden, $"Thread '{id}' is not allowed.");

        // With the broadcast sessions' output (#809): a thread's notify stream carries it, so the
        // history a client refetches after rockbot.resync has to as well.
        var history = await proxy.GetHistoryAsync(id, includeBroadcast: true, cancellationToken: ct);
        if (history is null)
            return Refusal(StatusCodes.Status504GatewayTimeout, "The agent did not answer the history request in time.");

        return Results.Text(AgUiJson.Serialize(AgUiHistoryMapper.ToSnapshot(id, history)), "application/json");
    }

    /// <summary>
    /// <c>POST /questions/{id}/answer</c>: answers or declines an MCP question, for a client that
    /// takes questions as <c>rockbot.mcp_question</c> events. The body is
    /// <c>{"threadId": "...", "answers": {...}}</c> or <c>{"threadId": "...", "decline": true}</c>.
    /// Answers <c>202</c>: what the agent makes of it (a status, then its reply) arrives on the
    /// notify stream.
    /// </summary>
    /// <remarks>
    /// A question this proxy doesn't know is not refused. The proxy only knows the questions it
    /// has heard of since it started, so the client may well be right; the answer goes to the
    /// agent, which reports <c>unavailable</c> if there is no such question.
    /// </remarks>
    private static async Task<IResult> AnswerQuestionAsync(
        string id, HttpContext context, UserProxyService proxy, AgUiOptions options, McpQuestionRegistry questions,
        CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            return Refusal(StatusCodes.Status400BadRequest, $"The body is not JSON: {ex.Message}");
        }

        if (body.ValueKind != JsonValueKind.Object)
            return Refusal(StatusCodes.Status400BadRequest, "The body must be a JSON object with answers or decline.");

        var known = questions.Find(id);

        // The thread the question is on: stated, or the one the proxy knows it by, or the only one there is.
        var threadId = body.TryGetProperty("threadId", out var stated) && stated.ValueKind == JsonValueKind.String
            ? stated.GetString()
            : known?.ThreadId ?? (options.ThreadIds() is [var only] ? only : null);
        if (string.IsNullOrEmpty(threadId))
            return Refusal(StatusCodes.Status400BadRequest, "threadId is required.");
        if (!options.IsThreadAllowed(threadId))
            return Refusal(StatusCodes.Status403Forbidden, $"Thread '{threadId}' is not allowed.");
        if (known is not null && !string.Equals(known.ThreadId, threadId, StringComparison.Ordinal))
            return Refusal(StatusCodes.Status404NotFound, $"Question '{id}' is not open on thread '{threadId}'.");

        var decline = body.TryGetProperty("decline", out var declined) && declined.ValueKind == JsonValueKind.True;
        string? answers = null;
        if (!decline)
        {
            if (!body.TryGetProperty("answers", out var given) || given.ValueKind != JsonValueKind.Object)
            {
                return Refusal(StatusCodes.Status400BadRequest,
                    "answers must be a JSON object of field values keyed by field name, or decline must be true.");
            }
            answers = given.GetRawText();
        }

        if (!proxy.IsConnected)
            return Refusal(StatusCodes.Status503ServiceUnavailable, "The proxy is not connected to the message bus.");

        await proxy.SendMcpAnswerAsync(new McpQuestionAnswer
        {
            SessionId = threadId,
            UserId = AgUiIdentity.UserId(context.User, options),
            QuestionId = id,
            Answers = answers,
            Decline = decline,
        }, ct);

        return Results.Json(new { questionId = id, threadId, known = known is not null }, statusCode: StatusCodes.Status202Accepted);
    }

    internal static IResult Refusal(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    private static Task RefuseAsync(HttpContext context, int status, string detail) =>
        Refusal(status, detail).ExecuteAsync(context);
}
