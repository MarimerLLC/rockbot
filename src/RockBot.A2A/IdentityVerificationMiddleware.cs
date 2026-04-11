using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Middleware that verifies the identity of inbound A2A messages using
/// <see cref="IAgentIdentityVerifier"/>. Stores the verified identity in
/// <see cref="MessageHandlerContext.Items"/> under the key
/// <see cref="ContextKey"/> for downstream handlers.
/// Only runs on A2A task-related messages; passes all other messages through unchanged.
/// </summary>
internal sealed class IdentityVerificationMiddleware(
    IAgentIdentityVerifier verifier,
    ILogger<IdentityVerificationMiddleware> logger) : IMiddleware
{
    public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
    {
        if (!IsA2AMessage(context.Envelope))
        {
            await next(context);
            return;
        }

        try
        {
            var identity = await verifier.VerifyAsync(context.Envelope, context.CancellationToken);
            context.Items[VerifiedAgentIdentity.ContextKey] = identity;
            logger.LogDebug("Verified inbound A2A identity: {AgentId} (self-asserted: {SelfAsserted})",
                identity.AgentId, identity.IsSelfAsserted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Identity verification failed for message {MessageId} from source '{Source}'",
                context.Envelope.MessageId, context.Envelope.Source);
            context.Result = MessageResult.DeadLetter;
            return;
        }

        await next(context);
    }

    private static bool IsA2AMessage(MessageEnvelope envelope)
    {
        var type = envelope.MessageType;
        return type.Contains(nameof(AgentTaskRequest), StringComparison.Ordinal) ||
               type.Contains(nameof(AgentTaskCancelRequest), StringComparison.Ordinal);
    }
}
