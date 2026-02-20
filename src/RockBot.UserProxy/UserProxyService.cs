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
    private ISubscription? _subscription;
    private ISubscription? _historySubscription;
    private bool _historyInitialized;
    private readonly SemaphoreSlim _historyInitLock = new(1, 1);
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
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke();

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

        if (_subscription is not null)
            await _subscription.DisposeAsync();

        if (_historySubscription is not null)
            await _historySubscription.DisposeAsync();

        _historyInitLock.Dispose();
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
