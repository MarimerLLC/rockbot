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
    private ISubscription? _subscription;
    private CancellationTokenSource? _cts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _subscription = await subscriber.SubscribeAsync(
            UserProxyTopics.UserResponse,
            $"user-proxy.{options.ProxyId}",
            HandleResponseAsync,
            cancellationToken);

        logger.LogInformation("User proxy {ProxyId} subscribed to {Topic}",
            options.ProxyId, UserProxyTopics.UserResponse);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel all pending requests
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var entry))
                entry.Tcs.TrySetCanceled();
        }

        if (_subscription is not null)
            await _subscription.DisposeAsync();

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
}
