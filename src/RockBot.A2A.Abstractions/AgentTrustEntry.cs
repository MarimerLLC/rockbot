namespace RockBot.A2A;

/// <summary>
/// Per-caller trust record tracking the trust level, approved skills, and
/// interaction history for an external agent identified by <see cref="AgentId"/>.
/// </summary>
public sealed record AgentTrustEntry
{
    /// <summary>Canonical unique identifier for the caller (from <see cref="VerifiedAgentIdentity.AgentId"/>).</summary>
    public required string AgentId { get; init; }

    /// <summary>Current trust level for this caller.</summary>
    public required AgentTrustLevel Level { get; init; }

    /// <summary>Skill IDs this caller is approved to invoke autonomously (Level 4).</summary>
    public IReadOnlyList<string> ApprovedSkills { get; init; } = [];

    /// <summary>When this caller was first seen.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>When the last interaction with this caller occurred.</summary>
    public DateTimeOffset LastInteraction { get; init; }

    /// <summary>Total number of inbound tasks received from this caller.</summary>
    public int InteractionCount { get; init; }

    /// <summary>
    /// Who vouched for this caller's identity on the most recent interaction
    /// (from <see cref="VerifiedAgentIdentity.Issuer"/>) — e.g. "self", an IdP issuer URL.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// True when the most recent interaction's identity was self-asserted (name-based) with
    /// no cryptographic/registry verification; false when it was independently verified
    /// (e.g. from gateway-forwarded JWT claims). Mirrors
    /// <see cref="VerifiedAgentIdentity.IsSelfAsserted"/>. Defaults to true (conservative).
    /// </summary>
    public bool IsSelfAsserted { get; init; } = true;
}
