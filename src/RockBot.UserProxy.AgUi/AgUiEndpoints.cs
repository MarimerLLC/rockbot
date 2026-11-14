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

/// <summary>The proxy's HTTP surface (#794): runs, the notify stream, cancel, history and health.</summary>
internal static class AgUiEndpoints
{
    /// <summary>How long a cancel request sent after the client went away may take.</summary>
    private static readonly TimeSpan AbortCancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The <c>RUN_ERROR</c> code for a run cut short because this proxy is shutting down.</summary>
    public const string ProxyStoppingCode = "proxy_stopping";

    public static IEndpointRouteBuilder MapAgUi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/agui", RunAsync);
        app.MapGet("/agui/notify", NotifyAsync);
        app.MapPost("/threads/{id}/cancel", CancelAsync);
        app.MapGet("/threads/{id}/messages", MessagesAsync);

        // For probes: ready only while the proxy holds its subscriptions on the bus.
        app.MapGet("/healthz", (UserProxyService proxy) =>
                proxy.IsConnected
                    ? Results.Text("ok")
                    : Results.Text("not connected to the message bus", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// <c>POST /agui</c>: one AG-UI run. Everything that can be refused is refused with a plain
    /// HTTP error before the first byte of the stream; after that, problems end the stream with
    /// <c>RUN_ERROR</c>.
    /// </summary>
    private static async Task RunAsync(
        HttpContext context,
        UserProxyService proxy,
        UserProxyOptions proxyOptions,
        AgUiOptions options,
        AgUiRunRegistry runs,
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

        var request = AgUiRequestMapper.Map(input, options, proxyOptions.ResolveChannelName());
        if (request.Message is not { } message)
        {
            await RefuseAsync(context, request.ErrorStatus, request.Error ?? "The request was refused.");
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
            await StreamRunAsync(context, proxy, options, message, input.RunId, lifetime.ApplicationStopping, logger);
        }
        finally
        {
            runs.Finish(input.RunId);
        }
    }

    private static async Task StreamRunAsync(
        HttpContext context, UserProxyService proxy, AgUiOptions options, UserMessage message, string runId,
        CancellationToken stopping, ILogger logger)
    {
        var response = StartEventStream(context);

        // The emitter is called from the bus's message-handling thread; the channel moves its
        // events to this request, which alone writes the response.
        var events = Channel.CreateUnbounded<BaseEvent>(new UnboundedChannelOptions { SingleReader = true });
        var emitter = new TurnEventEmitter(message.SessionId, runId, e => events.Writer.TryWrite(e));

        // Two reasons to stop waiting for the agent, handled differently: the client went away
        // (cancel the session, as AG-UI expects of an abort), or this proxy is shutting down (end
        // the stream and leave the agent to finish its work).
        using var clientGone = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(clientGone.Token, stopping);
        emitter.Start();
        var turn = RunTurnAsync(proxy, message, emitter, events.Writer, logger, stop.Token, clientGone.Token);

        try
        {
            await PumpAsync(events.Reader, response, options.KeepAliveInterval,
                e => $"data: {AgUiJson.Serialize(e)}\n\n", clientGone.Token);
        }
        catch (OperationCanceledException)
        {
            // The client went away; RunTurnAsync cancels the session.
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

        await turn;
    }

    /// <summary>Sends the turn and feeds its outcome to the emitter. Never throws; always completes the channel.</summary>
    private static async Task RunTurnAsync(
        UserProxyService proxy,
        UserMessage message,
        TurnEventEmitter emitter,
        ChannelWriter<BaseEvent> events,
        ILogger logger,
        CancellationToken stop,
        CancellationToken clientGone)
    {
        try
        {
            var result = await proxy.SendTurnAsync(
                message,
                new TurnOptions { CorrelationId = emitter.RunId, Progress = emitter, Observer = emitter },
                stop);

            logger.LogDebug("Run {RunId} on thread {ThreadId} ended as {Outcome}", emitter.RunId, emitter.ThreadId, result.Outcome);

            // Timed out: this proxy stopped waiting, but the agent may still be working. Its
            // reply then arrives uncorrelated and goes to the notify stream, so the run ends
            // normally and says so. Every other outcome is final.
            if (result.Outcome == TurnResultOutcome.TimedOut)
                emitter.FinishReplyPending(result);
            else
                emitter.Finish(result);
        }
        catch (OperationCanceledException) when (clientGone.IsCancellationRequested)
        {
            // AG-UI's way to cancel is to abort the request. Nobody is left to read the agent's
            // acknowledgement, so nothing more is emitted.
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
            logger.LogInformation("Ending run {RunId} on thread {ThreadId}: the proxy is stopping", emitter.RunId, emitter.ThreadId);
            emitter.Fail(ProxyStoppingCode, "The proxy is shutting down. The agent may still finish this turn.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} on thread {ThreadId} failed", emitter.RunId, emitter.ThreadId);
            emitter.Fail(TurnEventMapper.ErrorCodes.ClientError, ex.Message);
        }
        finally
        {
            events.TryComplete();
        }
    }

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

        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(lastEventId))
            lastEventId = context.Request.Query["lastEventId"].ToString();

        using var subscription = hub.Subscribe(threadId, lastEventId);
        if (subscription is null)
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, $"Thread '{threadId}' is not allowed.");
            return;
        }

        var response = StartEventStream(context);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping, subscription.Dropped);
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

        var history = await proxy.GetHistoryAsync(id, cancellationToken: ct);
        if (history is null)
            return Refusal(StatusCodes.Status504GatewayTimeout, "The agent did not answer the history request in time.");

        return Results.Text(AgUiJson.Serialize(AgUiHistoryMapper.ToSnapshot(id, history)), "application/json");
    }

    private static IResult Refusal(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    private static Task RefuseAsync(HttpContext context, int status, string detail) =>
        Refusal(status, detail).ExecuteAsync(context);
}
