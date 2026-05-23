namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// Topic constants for Work IQ auth-related bus messages.
/// </summary>
public static class WorkIqAuthTopics
{
    /// <summary>
    /// UI tier publishes a fresh MSAL cache to this topic after the user
    /// completes interactive consent. The agent's <c>TokenCacheStore</c>
    /// subscribes and persists the bytes to its PVC.
    /// </summary>
    public const string CacheUpdated = "auth.workiq.cache";

    /// <summary>
    /// The agent publishes to this topic when <c>AcquireTokenSilent</c>
    /// throws <c>MsalUiRequiredException</c>, signalling that the UI must
    /// prompt the user to reconnect.
    /// </summary>
    public const string Expired = "auth.workiq.expired";
}

/// <summary>
/// Carries a serialized MSAL token cache from the UI tier (Blazor / CLI)
/// to the agent after the user completes interactive consent.
/// </summary>
/// <remarks>
/// <see cref="CacheBytes"/> contains credential material (refresh token).
/// Never log this message's body and never persist it outside the agent's
/// secrets directory. The bus is already trusted with comparable secrets
/// (LLM prompts, working memory).
/// </remarks>
public sealed record WorkIqAuthCacheUpdated
{
    /// <summary>Opaque MSAL cache bytes — pass through to MSAL, do not parse.</summary>
    public required byte[] CacheBytes { get; init; }

    /// <summary>
    /// MSAL account identifier (HomeAccountId) shipped alongside for diagnostics
    /// without forcing the agent to instantiate MSAL just to inspect the cache.
    /// </summary>
    public required string AccountId { get; init; }

    /// <summary>Scopes the cache was issued for, for diagnostics only.</summary>
    public List<string> Scopes { get; init; } = [];
}

/// <summary>
/// Published by the agent when the MSAL cache can no longer produce a token
/// silently — typically because the refresh token has been revoked or aged out.
/// The UI tier subscribes to surface a reconnect prompt to the user.
/// </summary>
public sealed record WorkIqAuthExpired
{
    /// <summary>MSAL account identifier whose refresh token has expired.</summary>
    public required string AccountId { get; init; }

    /// <summary>Human-readable explanation suitable for logs and UI messages.</summary>
    public string? Reason { get; init; }
}
