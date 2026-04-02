using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Verifies the identity of an agent from an inbound message envelope.
/// Implementations may inspect headers (tokens, signatures), the Source field,
/// or any other envelope metadata to establish a verified identity.
/// Register a custom implementation via DI to replace the default name-based verifier.
/// </summary>
public interface IAgentIdentityVerifier
{
    /// <summary>
    /// Verifies the sender identity from the envelope metadata.
    /// Returns a <see cref="VerifiedAgentIdentity"/> on success, or throws if verification fails.
    /// </summary>
    Task<VerifiedAgentIdentity> VerifyAsync(MessageEnvelope envelope, CancellationToken ct);
}
