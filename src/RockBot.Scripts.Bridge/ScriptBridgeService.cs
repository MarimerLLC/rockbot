using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.Scripts;

namespace RockBot.Scripts.Bridge;

/// <summary>
/// Hosted service that listens for script invocation requests on the message bus,
/// executes them in ephemeral K8s pods via <see cref="IScriptRunner"/>,
/// and publishes results back to the ReplyTo topic.
/// </summary>
public sealed class ScriptBridgeService : IHostedService, IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly IScriptRunner _runner;
    private readonly ScriptBridgeOptions _options;
    private readonly ILogger<ScriptBridgeService> _logger;

    private ISubscription? _subscription;

    public ScriptBridgeService(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        IScriptRunner runner,
        IOptions<ScriptBridgeOptions> options,
        ILogger<ScriptBridgeService> logger)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Script bridge starting (agent={AgentName})", _options.AgentName);

        _subscription = await _subscriber.SubscribeAsync(
            "script.invoke",
            $"script-bridge.{_options.AgentName}",
            HandleScriptInvokeAsync,
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Script bridge stopping");

        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task<MessageResult> HandleScriptInvokeAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.GetPayload<ScriptInvokeRequest>();
        if (request is null)
        {
            _logger.LogWarning("Received script.invoke with null payload — dead-lettering");
            return MessageResult.DeadLetter;
        }

        var replyTo = envelope.ReplyTo ?? _options.DefaultResultTopic;
        var correlationId = envelope.CorrelationId;

        _logger.LogInformation("→ script {ToolCallId} timeout={Timeout}s", request.ToolCallId, request.TimeoutSeconds);

        ScriptInvokeResponse response;

        try
        {
            response = await _runner.ExecuteAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script {ToolCallId} runner threw unexpectedly", request.ToolCallId);

            response = new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Stderr = ex.Message,
                ExitCode = -1,
                ElapsedMs = 0
            };
        }

        if (response.IsSuccess)
            _logger.LogInformation("← script {ToolCallId} OK in {ElapsedMs}ms", request.ToolCallId, response.ElapsedMs);
        else
            _logger.LogWarning("← script {ToolCallId} exit={ExitCode} in {ElapsedMs}ms: {Stderr}",
                request.ToolCallId, response.ExitCode, response.ElapsedMs, response.Stderr);

        var outEnvelope = response.ToEnvelope(source: _options.AgentName, correlationId: correlationId);
        await _publisher.PublishAsync(replyTo, outEnvelope, ct);

        return MessageResult.Ack;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
