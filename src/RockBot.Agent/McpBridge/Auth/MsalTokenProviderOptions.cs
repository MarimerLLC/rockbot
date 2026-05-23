namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// Configuration for the MSAL-backed token provider used by the
/// <c>workiq</c> profile. Bound from the <c>WorkIQ</c> configuration section.
/// </summary>
public sealed class MsalTokenProviderOptions
{
    /// <summary>Entra tenant ID (GUID). Required when WorkIQ is enabled.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Public-client application ID registered in Entra. Shared between
    /// the UI tier (which performs interactive consent) and the agent
    /// (which silently refreshes the resulting cache).
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Delegated scopes to request at silent acquisition. For Work IQ these
    /// look like <c>WorkIQ-MailServer/.default</c>; the exact list depends on
    /// which Work IQ servers are enabled.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Path on disk where the MSAL token cache is persisted. Defaults to the
    /// shared agent secrets directory under the PVC mount.
    /// </summary>
    public string CacheFilePath { get; set; } = "/data/agent/secrets/workiq-cache.bin";

    /// <summary>
    /// Optional override of the OAuth authority URL. Defaults to
    /// <c>https://login.microsoftonline.com/{TenantId}</c>. Override for
    /// sovereign clouds (US Gov, China, etc.).
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// When true, this provider is registered with the bridge's token registry.
    /// Used by the host to gate the entire WorkIQ DI block on a single setting.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}
