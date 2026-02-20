using System.Collections.Concurrent;
using RockBot.Host;
using RockBot.Messaging;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Proxies tool invocations over the message bus to the MCP Bridge.
/// Publishes <see cref="ToolInvokeRequest"/> to <c>tool.invoke.mcp</c> and
/// waits for the correlated <see cref="ToolInvokeResponse"/> or <see cref="ToolError"/>
/// on <c>tool.result.{agentName}</c>.
/// </summary>
public sealed class McpToolProxy : IToolExecutor, IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly AgentIdentity _identity;
    private readonly ILogger<McpToolProxy> _logger;
    private readonly TimeSpan _timeout;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolInvokeResponse>> _pending = new();
    private ISubscription? _responseSubscription;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public const string InvokeTopic = "tool.invoke.mcp";

    public McpToolProxy(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        AgentIdentity identity,
        ILogger<McpToolProxy> logger,
        TimeSpan? timeout = null)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _identity = identity;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// The topic this proxy subscribes to for responses.
    /// </summary>
    public string ResponseTopic => $"tool.result.{_identity.Name}";

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
        => ExecuteAsync(request, null, ct);

    /// <summary>
    /// Publishes a tool invoke request with optional extra headers merged in.
    /// Used by <see cref="McpManagementExecutor"/> to attach the <c>rb-mcp-server</c>
    /// routing header when invoking via <c>mcp_invoke_tool</c>.
    /// </summary>
    internal async Task<ToolInvokeResponse> ExecuteAsync(
        ToolInvokeRequest request,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        await EnsureSubscribedAsync(ct);

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolInvokeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = tcs;

        try
        {
            var headers = new Dictionary<string, string>
            {
                [WellKnownHeaders.ContentTrust] = WellKnownHeaders.ContentTrustValues.ToolRequest,
                [WellKnownHeaders.ToolProvider] = "mcp",
                [WellKnownHeaders.TimeoutMs] = ((int)_timeout.TotalMilliseconds).ToString()
            };

            if (extraHeaders is not null)
            {
                foreach (var (key, value) in extraHeaders)
                    headers[key] = value;
            }

            var envelope = request.ToEnvelope(
                source: _identity.Name,
                correlationId: correlationId,
                replyTo: ResponseTopic,
                headers: headers);

            await _publisher.PublishAsync(InvokeTopic, envelope, ct);

            _logger.LogDebug("Published tool invoke request for {ToolName} with correlation {CorrelationId}",
                request.ToolName, correlationId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Tool invoke timed out for {ToolName} after {Timeout}ms",
                    request.ToolName, _timeout.TotalMilliseconds);

                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = $"Tool invocation timed out after {_timeout.TotalSeconds}s",
                    IsError = true
                };
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task EnsureSubscribedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _responseSubscription = await _subscriber.SubscribeAsync(
                ResponseTopic,
                $"mcp-proxy.{_identity.Name}",
                HandleResponseAsync,
                ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task<MessageResult> HandleResponseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.CorrelationId is null || !_pending.TryGetValue(envelope.CorrelationId, out var tcs))
        {
            _logger.LogWarning("Received tool response with unknown correlation ID: {CorrelationId}",
                envelope.CorrelationId);
            return Task.FromResult(MessageResult.Ack);
        }

        if (envelope.MessageType == typeof(ToolError).FullName)
        {
            var error = envelope.GetPayload<ToolError>();
            if (error is not null)
            {
                tcs.TrySetResult(new ToolInvokeResponse
                {
                    ToolCallId = error.ToolCallId,
                    ToolName = error.ToolName,
                    Content = error.Message,
                    IsError = true
                });
            }
        }
        else
        {
            var response = envelope.GetPayload<ToolInvokeResponse>();
            if (response is not null)
            {
                tcs.TrySetResult(response);
            }
        }

        return Task.FromResult(MessageResult.Ack);
    }

    public async ValueTask DisposeAsync()
    {
        if (_responseSubscription is not null)
        {
            await _responseSubscription.DisposeAsync();
        }

        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetCanceled();
        }
        _pending.Clear();
        _initLock.Dispose();
    }
}
