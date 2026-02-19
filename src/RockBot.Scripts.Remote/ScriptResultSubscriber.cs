using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.Scripts;

namespace RockBot.Scripts.Remote;

/// <summary>
/// Hosted service that subscribes to the agent's script result topic and
/// routes responses to the corresponding pending <see cref="MessageBusScriptRunner"/> request.
/// </summary>
internal sealed class ScriptResultSubscriber : IHostedService, IAsyncDisposable
{
    private readonly IMessageSubscriber _subscriber;
    private readonly MessageBusScriptRunner _runner;
    private readonly ILogger<ScriptResultSubscriber> _logger;

    private ISubscription? _subscription;

    public ScriptResultSubscriber(
        IMessageSubscriber subscriber,
        MessageBusScriptRunner runner,
        ILogger<ScriptResultSubscriber> logger)
    {
        _subscriber = subscriber;
        _runner = runner;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var topic = _runner.ReplyTopic;
        _logger.LogInformation("Subscribing to script result topic: {Topic}", topic);

        _subscription = await _subscriber.SubscribeAsync(
            topic,
            topic.Replace(".", "-"),
            HandleResultAsync,
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private Task<MessageResult> HandleResultAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var response = envelope.GetPayload<ScriptInvokeResponse>();
        if (response is null)
        {
            _logger.LogWarning("Received script result with null payload — dead-lettering");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        _runner.CompleteRequest(response);
        return Task.FromResult(MessageResult.Ack);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
