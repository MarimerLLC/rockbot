using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.UserProxy;

/// <summary>
/// Hosted service that bridges human users to the message bus.
/// Manages a subscription to agent replies and correlates them to pending requests.
/// </summary>
public sealed class UserProxyService(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    IUserFrontend frontend,
    UserProxyOptions options,
    ILogger<UserProxyService> logger) : IHostedService
{
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<AgentReply> Tcs, IProgress<AgentReply>? Progress)> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationHistoryResponse>> _pendingHistory = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentInfoResponse>> _pendingAgentInfo = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SaveResponseAck>> _pendingSaveResponse = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ListSavedResponsesResponse>> _pendingListSaved = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GetSavedResponseResponse>> _pendingGetSaved = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeleteSavedResponseAck>> _pendingDeleteSaved = new();
    private ISubscription? _subscription;
    private ISubscription? _historySubscription;
    private ISubscription? _agentInfoSubscription;
    private ISubscription? _saveResponseSubscription;
    private ISubscription? _listSavedSubscription;
    private ISubscription? _getSavedSubscription;
    private ISubscription? _deleteSavedSubscription;
    private bool _historyInitialized;
    private bool _agentInfoInitialized;
    private bool _saveResponseInitialized;
    private bool _listSavedInitialized;
    private bool _getSavedInitialized;
    private bool _deleteSavedInitialized;
    private readonly SemaphoreSlim _historyInitLock = new(1, 1);
    private readonly SemaphoreSlim _agentInfoInitLock = new(1, 1);
    private readonly SemaphoreSlim _saveResponseInitLock = new(1, 1);
    private readonly SemaphoreSlim _listSavedInitLock = new(1, 1);
    private readonly SemaphoreSlim _getSavedInitLock = new(1, 1);
    private readonly SemaphoreSlim _deleteSavedInitLock = new(1, 1);
    private CancellationTokenSource? _cts;

    public bool IsConnected { get; private set; }
    public event Action? OnConnectionChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _subscription = await subscriber.SubscribeAsync(
                UserProxyTopics.UserResponse,
                $"user-proxy.{options.ProxyId}",
                HandleResponseAsync,
                cancellationToken);

            IsConnected = true;
            OnConnectionChanged?.Invoke();
            logger.LogInformation("User proxy {ProxyId} subscribed to {Topic}",
                options.ProxyId, UserProxyTopics.UserResponse);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            OnConnectionChanged?.Invoke();
            logger.LogError(ex, "User proxy {ProxyId} failed to subscribe to {Topic}",
                options.ProxyId, UserProxyTopics.UserResponse);

            if (options.MaxSubscribeRetries > 0)
            {
                // Fire-and-forget: retry in the background using the linked CTS
                _ = RetrySubscribeAsync(_cts.Token);
            }
        }
    }

    /// <summary>
    /// Retries the <c>user.response</c> subscription with exponential backoff until it
    /// succeeds or the service is stopped.
    /// </summary>
    private async Task RetrySubscribeAsync(CancellationToken ct)
    {
        var delay = options.SubscribeRetryBaseDelay;

        for (var attempt = 1; attempt <= options.MaxSubscribeRetries; attempt++)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping — exit silently
                return;
            }

            try
            {
                _subscription = await subscriber.SubscribeAsync(
                    UserProxyTopics.UserResponse,
                    $"user-proxy.{options.ProxyId}",
                    HandleResponseAsync,
                    ct);

                IsConnected = true;
                OnConnectionChanged?.Invoke();
                logger.LogInformation(
                    "User proxy {ProxyId} subscribed to {Topic} on retry attempt {Attempt}",
                    options.ProxyId, UserProxyTopics.UserResponse, attempt);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "User proxy {ProxyId} retry attempt {Attempt} failed for {Topic}",
                    options.ProxyId, attempt, UserProxyTopics.UserResponse);

                // Exponential backoff capped at MaxSubscribeRetryDelay
                delay = TimeSpan.FromTicks(Math.Min(
                    delay.Ticks * 2,
                    options.MaxSubscribeRetryDelay.Ticks));
            }
        }

        logger.LogError(
            "User proxy {ProxyId} exhausted all {MaxRetries} retry attempts for {Topic}",
            options.ProxyId, options.MaxSubscribeRetries, UserProxyTopics.UserResponse);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke();

        // Signal the retry loop (and any other linked work) to stop.
        // ObjectDisposedException can occur when StopAsync is called more than once
        // (e.g. test cleanup after an explicit stop) — safe to ignore during shutdown.
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        // Cancel all pending requests
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var entry))
                entry.Tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingHistory)
        {
            if (_pendingHistory.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingAgentInfo)
        {
            if (_pendingAgentInfo.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingSaveResponse)
        {
            if (_pendingSaveResponse.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingListSaved)
        {
            if (_pendingListSaved.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingGetSaved)
        {
            if (_pendingGetSaved.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        foreach (var kvp in _pendingDeleteSaved)
        {
            if (_pendingDeleteSaved.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }

        if (_subscription is not null)
            await _subscription.DisposeAsync();

        if (_historySubscription is not null)
            await _historySubscription.DisposeAsync();

        if (_agentInfoSubscription is not null)
            await _agentInfoSubscription.DisposeAsync();

        if (_saveResponseSubscription is not null)
            await _saveResponseSubscription.DisposeAsync();

        if (_listSavedSubscription is not null)
            await _listSavedSubscription.DisposeAsync();

        if (_getSavedSubscription is not null)
            await _getSavedSubscription.DisposeAsync();

        if (_deleteSavedSubscription is not null)
            await _deleteSavedSubscription.DisposeAsync();

        _historyInitLock.Dispose();
        _agentInfoInitLock.Dispose();
        _saveResponseInitLock.Dispose();
        _listSavedInitLock.Dispose();
        _getSavedInitLock.Dispose();
        _deleteSavedInitLock.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    /// Sends a user message and awaits the correlated agent reply.
    /// Intermediate replies (<see cref="AgentReply.IsFinal"/> = false) are reported via
    /// <paramref name="progress"/> without resolving the returned task.
    /// </summary>
    /// <returns>The agent reply, or null if the timeout elapsed.</returns>
    public async Task<AgentReply?> SendAsync(
        UserMessage message,
        IProgress<AgentReply>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AgentReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[correlationId] = (tcs, progress);

        using var activity = UserProxyDiagnostics.Source.StartActivity("UserProxy.Send");
        activity?.SetTag("rockbot.proxy.correlation_id", correlationId);
        var sw = Stopwatch.StartNew();

        try
        {
            var envelope = message.ToEnvelope<UserMessage>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: UserProxyTopics.UserResponse,
                destination: message.TargetAgent);

            await publisher.PublishAsync(UserProxyTopics.UserMessage, envelope, cancellationToken);
            UserProxyDiagnostics.MessagesSent.Add(1);

            logger.LogDebug("Published user message {CorrelationId} to {Topic}",
                correlationId, UserProxyTopics.UserMessage);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                var reply = await tcs.Task.WaitAsync(timeoutCts.Token);
                sw.Stop();
                UserProxyDiagnostics.RoundtripDuration.Record(sw.Elapsed.TotalMilliseconds);
                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout — not external cancellation
                logger.LogWarning("Reply timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Publishes thumbs-up or thumbs-down feedback for a specific agent reply.
    /// Fire-and-forget: the agent is expected to react without sending a direct reply
    /// for positive feedback, or to re-evaluate and send an unsolicited reply for negative feedback.
    /// </summary>
    public async Task SendFeedbackAsync(
        UserFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        var envelope = feedback.ToEnvelope<UserFeedback>(
            source: options.ProxyId,
            destination: feedback.AgentName);

        await publisher.PublishAsync(UserProxyTopics.UserFeedback, envelope, cancellationToken);

        logger.LogDebug("Published {FeedbackType} feedback for message {MessageId} to {Agent}",
            feedback.IsPositive ? "positive" : "negative",
            feedback.MessageId,
            feedback.AgentName ?? "(broadcast)");
    }

    /// <summary>
    /// Sends a user message without waiting for a reply.
    /// </summary>
    public async Task SendFireAndForgetAsync(
        UserMessage message,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        var envelope = message.ToEnvelope<UserMessage>(
            source: options.ProxyId,
            correlationId: correlationId,
            replyTo: UserProxyTopics.UserResponse,
            destination: message.TargetAgent);

        await publisher.PublishAsync(UserProxyTopics.UserMessage, envelope, cancellationToken);
        UserProxyDiagnostics.MessagesSent.Add(1);

        logger.LogDebug("Published fire-and-forget user message {CorrelationId}", correlationId);
    }

    /// <summary>
    /// Publishes a cancel request to stop all in-flight work for the given session.
    /// </summary>
    public async Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var request = new CancelSessionRequest { SessionId = sessionId };
        var envelope = request.ToEnvelope<CancelSessionRequest>(source: options.ProxyId);
        await publisher.PublishAsync(UserProxyTopics.CancelSession, envelope, cancellationToken);
        logger.LogInformation("Published cancel request for session {SessionId}", sessionId);
    }

    internal Task<MessageResult> HandleResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        AgentReply? reply;
        try
        {
            reply = envelope.GetPayload<AgentReply>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize AgentReply");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (reply is null || string.IsNullOrEmpty(reply.Content))
        {
            logger.LogWarning("Received invalid AgentReply (null or empty content)");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        UserProxyDiagnostics.RepliesReceived.Add(1);

        if (envelope.CorrelationId is not null &&
            _pending.TryGetValue(envelope.CorrelationId, out var entry))
        {
            if (reply.IsFinal)
            {
                // Final reply: resolve the pending request
                _pending.TryRemove(envelope.CorrelationId, out _);
                entry.Tcs.TrySetResult(reply);
                logger.LogDebug("Final reply correlated for {CorrelationId} from {Agent}",
                    envelope.CorrelationId, reply.AgentName);
            }
            else
            {
                // Intermediate reply (IsFinal=false): report progress without resolving
                entry.Progress?.Report(reply);
                logger.LogDebug("Intermediate reply for {CorrelationId} from {Agent}; progress reported",
                    envelope.CorrelationId, reply.AgentName);
            }
        }
        else
        {
            // Unsolicited reply — display via frontend
            logger.LogDebug("Unsolicited reply from {Agent}, displaying via frontend", reply.AgentName);
            _ = frontend.DisplayReplyAsync(reply, ct);
        }

        return Task.FromResult(MessageResult.Ack);
    }

    /// <summary>
    /// Requests the full conversation history for the given session from the agent.
    /// Returns null if the request times out or the agent is unavailable.
    /// </summary>
    public async Task<ConversationHistoryResponse?> GetHistoryAsync(
        string sessionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ConversationHistoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingHistory[correlationId] = tcs;

        try
        {
            await EnsureHistorySubscribedAsync(cancellationToken);

            var request = new ConversationHistoryRequest { SessionId = sessionId };
            var envelope = request.ToEnvelope<ConversationHistoryRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: HistoryResponseTopic);

            await publisher.PublishAsync(UserProxyTopics.ConversationHistoryRequest, envelope, cancellationToken);

            logger.LogDebug("Published ConversationHistoryRequest {CorrelationId} for session {SessionId}",
                correlationId, sessionId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("History request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingHistory.TryRemove(correlationId, out _);
        }
    }

    private string HistoryResponseTopic => $"{UserProxyTopics.ConversationHistoryResponse}.{options.ProxyId}";

    private async Task EnsureHistorySubscribedAsync(CancellationToken ct)
    {
        if (_historyInitialized) return;

        await _historyInitLock.WaitAsync(ct);
        try
        {
            if (_historyInitialized) return;

            _historySubscription = await subscriber.SubscribeAsync(
                HistoryResponseTopic,
                $"user-proxy.{options.ProxyId}.history",
                HandleHistoryResponseAsync,
                ct);

            _historyInitialized = true;
        }
        finally
        {
            _historyInitLock.Release();
        }
    }

    /// <summary>
    /// Requests agent identity metadata (name, version) from the agent.
    /// Returns null if the request times out or the agent is unavailable.
    /// </summary>
    public async Task<AgentInfoResponse?> GetAgentInfoAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AgentInfoResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingAgentInfo[correlationId] = tcs;

        try
        {
            await EnsureAgentInfoSubscribedAsync(cancellationToken);

            var request = new AgentInfoRequest();
            var envelope = request.ToEnvelope<AgentInfoRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: AgentInfoResponseTopic);

            await publisher.PublishAsync(UserProxyTopics.AgentInfoRequest, envelope, cancellationToken);

            logger.LogDebug("Published AgentInfoRequest {CorrelationId}", correlationId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Agent info request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingAgentInfo.TryRemove(correlationId, out _);
        }
    }

    private string AgentInfoResponseTopic => $"{UserProxyTopics.AgentInfoResponse}.{options.ProxyId}";

    private async Task EnsureAgentInfoSubscribedAsync(CancellationToken ct)
    {
        if (_agentInfoInitialized) return;

        await _agentInfoInitLock.WaitAsync(ct);
        try
        {
            if (_agentInfoInitialized) return;

            _agentInfoSubscription = await subscriber.SubscribeAsync(
                AgentInfoResponseTopic,
                $"user-proxy.{options.ProxyId}.agent-info",
                HandleAgentInfoResponseAsync,
                ct);

            _agentInfoInitialized = true;
        }
        finally
        {
            _agentInfoInitLock.Release();
        }
    }

    internal Task<MessageResult> HandleAgentInfoResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingAgentInfo.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received agent info response with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        AgentInfoResponse? response;
        try
        {
            response = envelope.GetPayload<AgentInfoResponse>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize AgentInfoResponse");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null AgentInfoResponse");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingAgentInfo.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("Agent info response correlated for {CorrelationId}: {Name} v{Version}",
            envelope.CorrelationId, response.AgentName, response.AgentVersion);

        return Task.FromResult(MessageResult.Ack);
    }

    // ── Saved responses ───────────────────────────────────────────────────────

    /// <summary>
    /// Saves an agent response on the agent server. Returns the ack with the assigned ID.
    /// </summary>
    public async Task<SaveResponseAck?> SaveResponseAsync(
        SaveResponseRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<SaveResponseAck>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingSaveResponse[correlationId] = tcs;

        try
        {
            await EnsureSaveResponseSubscribedAsync(cancellationToken);

            var envelope = request.ToEnvelope<SaveResponseRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: SaveResponseAckTopic);

            await publisher.PublishAsync(UserProxyTopics.SaveResponseRequest, envelope, cancellationToken);

            logger.LogDebug("Published SaveResponseRequest {CorrelationId}", correlationId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Save response request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingSaveResponse.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Lists all saved responses from the agent server.
    /// </summary>
    public async Task<ListSavedResponsesResponse?> ListSavedResponsesAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ListSavedResponsesResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingListSaved[correlationId] = tcs;

        try
        {
            await EnsureListSavedSubscribedAsync(cancellationToken);

            var request = new ListSavedResponsesRequest();
            var envelope = request.ToEnvelope<ListSavedResponsesRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: ListSavedResponsesTopic);

            await publisher.PublishAsync(UserProxyTopics.ListSavedResponsesRequest, envelope, cancellationToken);

            logger.LogDebug("Published ListSavedResponsesRequest {CorrelationId}", correlationId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("List saved responses request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingListSaved.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Retrieves a single saved response by ID from the agent server.
    /// </summary>
    public async Task<GetSavedResponseResponse?> GetSavedResponseAsync(
        string id,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GetSavedResponseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingGetSaved[correlationId] = tcs;

        try
        {
            await EnsureGetSavedSubscribedAsync(cancellationToken);

            var request = new GetSavedResponseRequest { Id = id };
            var envelope = request.ToEnvelope<GetSavedResponseRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: GetSavedResponseTopic);

            await publisher.PublishAsync(UserProxyTopics.GetSavedResponseRequest, envelope, cancellationToken);

            logger.LogDebug("Published GetSavedResponseRequest {CorrelationId} for {Id}", correlationId, id);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Get saved response request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingGetSaved.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Deletes a saved response by ID from the agent server.
    /// </summary>
    public async Task<DeleteSavedResponseAck?> DeleteSavedResponseAsync(
        string id,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? options.DefaultReplyTimeout;
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DeleteSavedResponseAck>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingDeleteSaved[correlationId] = tcs;

        try
        {
            await EnsureDeleteSavedSubscribedAsync(cancellationToken);

            var request = new DeleteSavedResponseRequest { Id = id };
            var envelope = request.ToEnvelope<DeleteSavedResponseRequest>(
                source: options.ProxyId,
                correlationId: correlationId,
                replyTo: DeleteSavedAckTopic);

            await publisher.PublishAsync(UserProxyTopics.DeleteSavedResponseRequest, envelope, cancellationToken);

            logger.LogDebug("Published DeleteSavedResponseRequest {CorrelationId} for {Id}", correlationId, id);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Delete saved response request timeout for correlation {CorrelationId} after {Timeout}",
                    correlationId, effectiveTimeout);
                return null;
            }
        }
        finally
        {
            _pendingDeleteSaved.TryRemove(correlationId, out _);
        }
    }

    // ── Saved response topics ────────────────────────────────────────────────

    private string SaveResponseAckTopic => $"{UserProxyTopics.SaveResponseAck}.{options.ProxyId}";
    private string ListSavedResponsesTopic => $"{UserProxyTopics.ListSavedResponsesResponse}.{options.ProxyId}";
    private string GetSavedResponseTopic => $"{UserProxyTopics.GetSavedResponseResponse}.{options.ProxyId}";
    private string DeleteSavedAckTopic => $"{UserProxyTopics.DeleteSavedResponseAck}.{options.ProxyId}";

    // ── Saved response subscription setup ────────────────────────────────────

    private async Task EnsureSaveResponseSubscribedAsync(CancellationToken ct)
    {
        if (_saveResponseInitialized) return;

        await _saveResponseInitLock.WaitAsync(ct);
        try
        {
            if (_saveResponseInitialized) return;

            _saveResponseSubscription = await subscriber.SubscribeAsync(
                SaveResponseAckTopic,
                $"user-proxy.{options.ProxyId}.save-response",
                HandleSaveResponseAckAsync,
                ct);

            _saveResponseInitialized = true;
        }
        finally
        {
            _saveResponseInitLock.Release();
        }
    }

    private async Task EnsureListSavedSubscribedAsync(CancellationToken ct)
    {
        if (_listSavedInitialized) return;

        await _listSavedInitLock.WaitAsync(ct);
        try
        {
            if (_listSavedInitialized) return;

            _listSavedSubscription = await subscriber.SubscribeAsync(
                ListSavedResponsesTopic,
                $"user-proxy.{options.ProxyId}.list-saved",
                HandleListSavedResponseAsync,
                ct);

            _listSavedInitialized = true;
        }
        finally
        {
            _listSavedInitLock.Release();
        }
    }

    private async Task EnsureGetSavedSubscribedAsync(CancellationToken ct)
    {
        if (_getSavedInitialized) return;

        await _getSavedInitLock.WaitAsync(ct);
        try
        {
            if (_getSavedInitialized) return;

            _getSavedSubscription = await subscriber.SubscribeAsync(
                GetSavedResponseTopic,
                $"user-proxy.{options.ProxyId}.get-saved",
                HandleGetSavedResponseAsync,
                ct);

            _getSavedInitialized = true;
        }
        finally
        {
            _getSavedInitLock.Release();
        }
    }

    private async Task EnsureDeleteSavedSubscribedAsync(CancellationToken ct)
    {
        if (_deleteSavedInitialized) return;

        await _deleteSavedInitLock.WaitAsync(ct);
        try
        {
            if (_deleteSavedInitialized) return;

            _deleteSavedSubscription = await subscriber.SubscribeAsync(
                DeleteSavedAckTopic,
                $"user-proxy.{options.ProxyId}.delete-saved",
                HandleDeleteSavedAckAsync,
                ct);

            _deleteSavedInitialized = true;
        }
        finally
        {
            _deleteSavedInitLock.Release();
        }
    }

    // ── Saved response handlers ──────────────────────────────────────────────

    internal Task<MessageResult> HandleSaveResponseAckAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingSaveResponse.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received save response ack with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        SaveResponseAck? response;
        try
        {
            response = envelope.GetPayload<SaveResponseAck>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize SaveResponseAck");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null SaveResponseAck");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingSaveResponse.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("Save response ack correlated for {CorrelationId}: Id={Id}", envelope.CorrelationId, response.Id);

        return Task.FromResult(MessageResult.Ack);
    }

    internal Task<MessageResult> HandleListSavedResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingListSaved.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received list saved responses with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        ListSavedResponsesResponse? response;
        try
        {
            response = envelope.GetPayload<ListSavedResponsesResponse>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize ListSavedResponsesResponse");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null ListSavedResponsesResponse");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingListSaved.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("List saved responses correlated for {CorrelationId} with {Count} items",
            envelope.CorrelationId, response.Items.Count);

        return Task.FromResult(MessageResult.Ack);
    }

    internal Task<MessageResult> HandleGetSavedResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingGetSaved.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received get saved response with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        GetSavedResponseResponse? response;
        try
        {
            response = envelope.GetPayload<GetSavedResponseResponse>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize GetSavedResponseResponse");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null GetSavedResponseResponse");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingGetSaved.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("Get saved response correlated for {CorrelationId}: Id={Id} Found={Found}",
            envelope.CorrelationId, response.Id, response.Found);

        return Task.FromResult(MessageResult.Ack);
    }

    internal Task<MessageResult> HandleDeleteSavedAckAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingDeleteSaved.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received delete saved response ack with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        DeleteSavedResponseAck? response;
        try
        {
            response = envelope.GetPayload<DeleteSavedResponseAck>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize DeleteSavedResponseAck");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null DeleteSavedResponseAck");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingDeleteSaved.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("Delete saved response ack correlated for {CorrelationId}", envelope.CorrelationId);

        return Task.FromResult(MessageResult.Ack);
    }

    // ── History / Agent info ─────────────────────────────────────────────────

    internal Task<MessageResult> HandleHistoryResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null ||
            !_pendingHistory.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            logger.LogWarning("Received history response with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        ConversationHistoryResponse? response;
        try
        {
            response = envelope.GetPayload<ConversationHistoryResponse>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize ConversationHistoryResponse");
            tcs.TrySetException(ex);
            return Task.FromResult(MessageResult.DeadLetter);
        }

        if (response is null)
        {
            logger.LogWarning("Received null ConversationHistoryResponse");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _pendingHistory.TryRemove(envelope.CorrelationId, out _);
        tcs.TrySetResult(response);

        logger.LogDebug("History response correlated for {CorrelationId} with {TurnCount} turns",
            envelope.CorrelationId, response.Turns.Count);

        return Task.FromResult(MessageResult.Ack);
    }
}
