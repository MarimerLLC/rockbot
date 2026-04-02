namespace RockBot.A2A;

/// <summary>
/// The result of identity verification for an inbound agent message.
/// <see cref="AgentId"/> is the stable key used for trust tracking.
/// </summary>
public sealed record VerifiedAgentIdentity
{
    /// <summary>
    /// Key used to store/retrieve <see cref="VerifiedAgentIdentity"/> in
    /// <see cref="RockBot.Host.MessageHandlerContext.Items"/>.
    /// </summary>
    public const string ContextKey = "verified-identity";

    /// <summary>
    /// Canonical unique identifier for the agent. Used as the key in trust stores.
    /// For name-based verification this equals the Source string; for registry-backed
    /// verification it would be a registry-issued identifier.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>Human-readable display name for the agent.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Who vouched for this identity (e.g. "self", a registry URL, an IdP issuer).
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// Extensible claims extracted during verification (e.g. roles, scopes, OBO subject).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Claims { get; init; }

    /// <summary>
    /// True when identity is based solely on the sender's self-asserted Source string
    /// with no cryptographic or registry-backed verification.
    /// </summary>
    public bool IsSelfAsserted { get; init; }
}
