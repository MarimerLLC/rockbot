namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// Settings for the UI-tier WorkIQ MSAL flow. Bound from the <c>WorkIQ</c>
/// configuration section. Mirrors the agent-side <c>MsalTokenProviderOptions</c>
/// so the same Entra app registration values can be written once and consumed
/// by every process that touches WorkIQ.
/// </summary>
public sealed class WorkIqClientSettings
{
    /// <summary>Entra tenant ID (GUID). Required.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Public-client application ID registered in Entra. Shared between
    /// the UI tier (which performs interactive consent) and the agent
    /// (which silently refreshes the resulting cache).
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Delegated scopes requested at consent. Typically per-server scopes
    /// like <c>WorkIQ-MailServer/.default</c>, <c>WorkIQ-Calendar/.default</c>.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Optional override of the OAuth authority URL. Defaults to
    /// <c>https://login.microsoftonline.com/{TenantId}</c>. Override for
    /// sovereign clouds (US Gov, China, etc.).
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Whether the settings are populated enough to run a flow.
    /// </summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}
