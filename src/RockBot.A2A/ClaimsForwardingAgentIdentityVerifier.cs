using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Identity verifier that trusts caller claims forwarded by an upstream gateway that has
/// already validated them (e.g. from a JWT). When the envelope carries the
/// <see cref="WellKnownHeaders.AuthClaims"/> header, the resulting identity is marked
/// <see cref="VerifiedAgentIdentity.IsSelfAsserted"/> = <c>false</c>, with the IdP issuer
/// and verified claims populated. When the header is absent, verification falls back to the
/// supplied name-based verifier (self-asserted Source string).
/// </summary>
internal sealed class ClaimsForwardingAgentIdentityVerifier(
    NameBasedAgentIdentityVerifier fallback,
    ILogger<ClaimsForwardingAgentIdentityVerifier> logger) : IAgentIdentityVerifier
{
    public Task<VerifiedAgentIdentity> VerifyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (!envelope.Headers.TryGetValue(WellKnownHeaders.AuthClaims, out var claimsJson)
            || string.IsNullOrWhiteSpace(claimsJson))
        {
            // No gateway-verified claims — trust the Source string at face value.
            return fallback.VerifyAsync(envelope, ct);
        }

        Dictionary<string, string>? claims;
        try
        {
            claims = JsonSerializer.Deserialize<Dictionary<string, string>>(claimsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Cannot verify identity: forwarded auth claims header is not valid JSON.", ex);
        }

        if (claims is null || claims.Count == 0)
            throw new InvalidOperationException("Cannot verify identity: forwarded auth claims are empty.");

        // "sub" is the verified caller id; fall back to Source only as a last resort.
        var agentId = claims.GetValueOrDefault("sub");
        if (string.IsNullOrWhiteSpace(agentId))
            agentId = envelope.Source;
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Cannot verify identity: forwarded claims have no subject.");

        var displayName = claims.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = agentId;

        var identity = new VerifiedAgentIdentity
        {
            AgentId = agentId,
            DisplayName = displayName,
            Issuer = claims.GetValueOrDefault("iss") ?? "gateway",
            Claims = claims,
            IsSelfAsserted = false
        };

        logger.LogDebug("Verified inbound A2A identity from forwarded claims: {AgentId} (issuer: {Issuer})",
            identity.AgentId, identity.Issuer);

        return Task.FromResult(identity);
    }
}
