using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Prototype identity verifier that trusts the envelope's Source field at face value.
/// Returns <see cref="VerifiedAgentIdentity.IsSelfAsserted"/> = true to indicate
/// no cryptographic or registry-backed verification was performed.
/// Replace via DI with a custom <see cref="IAgentIdentityVerifier"/> for production use.
/// </summary>
internal sealed class NameBasedAgentIdentityVerifier : IAgentIdentityVerifier
{
    public Task<VerifiedAgentIdentity> VerifyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope.Source))
            throw new InvalidOperationException("Cannot verify identity: envelope Source is empty.");

        var identity = new VerifiedAgentIdentity
        {
            AgentId = envelope.Source,
            DisplayName = envelope.Source,
            Issuer = "self",
            IsSelfAsserted = true
        };
        return Task.FromResult(identity);
    }
}
