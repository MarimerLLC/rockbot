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
    /// Publishes an event that belongs to the run described by <paramref name="context"/>. A
    /// correlated run whose proxy advertised <see cref="ClientCapabilities.AgentEvents"/> gets it
    /// on its reply topic with its correlation id; a correlated run whose proxy didn't, doesn't get
    /// it at all (that proxy would mistake it for a reply); an uncorrelated run (a subagent, a
    /// scheduled task) has it broadcast on <see cref="UserProxyTopics.UserEvent"/>.
    /// </summary>
    Task PublishForRunAsync<T>(T evt, ToolProgressContext context, CancellationToken ct = default) where T : class;

    /// <summary>
    /// <see cref="PublishForRunAsync{T}(T, ToolProgressContext, CancellationToken)"/> for the run in
    /// the ambient <see cref="ToolProgressContext"/>. Without one, the event is dropped.
    /// </summary>
    Task PublishForRunAsync<T>(T evt, CancellationToken ct = default) where T : class =>
        ToolProgressNotifier.Current is { } context ? PublishForRunAsync(evt, context, ct) : Task.CompletedTask;

    /// <summary>Broadcasts an event on <see cref="UserProxyTopics.UserEvent"/>.</summary>
    Task BroadcastAsync<T>(T evt, CancellationToken ct = default) where T : class;
}

/// <summary>Default <see cref="IAgentEventPublisher"/>. Swallows and logs publish failures.</summary>
public sealed class AgentEventPublisher(
    IMessagePublisher publisher,
    AgentIdentity agent,
    ILogger<AgentEventPublisher> logger) : IAgentEventPublisher
{
    public async Task PublishForRunAsync<T>(T evt, ToolProgressContext context, CancellationToken ct = default) where T : class
    {
        if (context.CorrelationId is null)
        {
            await BroadcastAsync(evt, ct);
            return;
        }

        if (!context.EventsEnabled)
            return;

        await PublishAsync(context.ReplyTo, evt.ToEnvelope<T>(source: agent.Name, correlationId: context.CorrelationId), ct);
    }

    public Task PublishForRunAsync<T>(T evt, CancellationToken ct = default) where T : class =>
        ToolProgressNotifier.Current is { } context ? PublishForRunAsync(evt, context, ct) : Task.CompletedTask;

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
