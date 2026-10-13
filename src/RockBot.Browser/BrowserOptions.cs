namespace RockBot.Browser;

/// <summary>
/// Settings for the render service, bound from the <c>Browser</c> configuration section.
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>Pages rendered at once. Each holds a Chromium renderer, so keep it small. Defaults to 2.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Page budget when the request names none, in milliseconds. Defaults to 20000.</summary>
    public int DefaultTimeoutMs { get; set; } = 20_000;

    /// <summary>Largest page budget a request may ask for, in milliseconds. Defaults to 45000.</summary>
    public int MaxTimeoutMs { get; set; } = 45_000;

    /// <summary>
    /// How long to wait for the network to go quiet after load (and again after dismissing a
    /// consent banner), in milliseconds. Pages that never go quiet are read anyway. Defaults to 5000.
    /// </summary>
    public int SettleMs { get; set; } = 5_000;

    /// <summary>
    /// Lets pages reach loopback, private and link-local addresses. Off in production: the
    /// sidecar shares the agent pod's network, so a page could otherwise reach the agent's
    /// own endpoints. Only for local testing against a dev server.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}
