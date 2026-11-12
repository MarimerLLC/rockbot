using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Publishes structured events for user proxies (#795): run lifecycle, task lists, subagent
/// lifecycle and active-work snapshots.
/// </summary>
public interface IAgentEventPublisher
{
    /// <summary>
    /// Publishes an event that belongs to the run in the ambient <see cref="ToolProgressContext"/>.
    /// A correlated run whose proxy advertised <see cref="ClientCapabilities.AgentEvents"/> gets it
    /// on its reply topic with its correlation id; a correlated run whose proxy didn't, doesn't
    /// get it at all (that proxy would mistake it for a reply); an uncorrelated run (a subagent,
    /// a scheduled task) has it broadcast on <see cref="UserProxyTopics.UserEvent"/>. Without an
    /// ambient context the event is dropped.
    /// </summary>
    Task PublishForRunAsync<T>(T evt, CancellationToken ct = default) where T : class;

    /// <summary>Broadcasts an event on <see cref="UserProxyTopics.UserEvent"/>.</summary>
    Task BroadcastAsync<T>(T evt, CancellationToken ct = default) where T : class;
}

/// <summary>Default <see cref="IAgentEventPublisher"/>. Swallows and logs publish failures.</summary>
public sealed class AgentEventPublisher(
    IMessagePublisher publisher,
    AgentIdentity agent,
    ILogger<AgentEventPublisher> logger) : IAgentEventPublisher
{
    public async Task PublishForRunAsync<T>(T evt, CancellationToken ct = default) where T : class
    {
        var ctx = ToolProgressNotifier.Current;
        if (ctx is null)
            return;

        if (ctx.CorrelationId is null)
        {
            await BroadcastAsync(evt, ct);
            return;
        }

        if (!ctx.EventsEnabled)
            return;

        await PublishAsync(ctx.ReplyTo, evt.ToEnvelope<T>(source: agent.Name, correlationId: ctx.CorrelationId), ct);
    }

    public Task BroadcastAsync<T>(T evt, CancellationToken ct = default) where T : class =>
        PublishAsync($"{UserProxyTopics.UserEvent}.{agent.Name}", evt.ToEnvelope<T>(source: agent.Name), ct);

    private async Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            await publisher.PublishAsync(topic, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to publish {Type} to {Topic}", envelope.MessageType, topic);
        }
    }
}
