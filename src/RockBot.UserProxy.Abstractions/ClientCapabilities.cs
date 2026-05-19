namespace RockBot.UserProxy;

/// <summary>
/// Rendering capabilities the receiving client advertises. Treat as a forward-compatible
/// bitfield — bits added later by newer proxies appear as unknown values on older agents
/// and are safely ignored by <see cref="Enum.HasFlag(Enum)"/> and mask checks. Bit
/// layout leaves gaps so growth is organized: text formatting in bits 0–15, rich
/// rendering in bits 16–31, platform-native UI primitives in bits 32–47.
/// </summary>
[Flags]
public enum ClientCapabilities : ulong
{
    None              = 0,

    // Text + markdown subsets (bits 0–15)
    Text                  = 1UL << 0,    // implicit floor — every client supports this
    MarkdownBasic         = 1UL << 1,    // bold, italic, inline code, blockquotes
    MarkdownHeadings      = 1UL << 2,    // # / ## / ###
    MarkdownTables        = 1UL << 3,    // GFM tables
    MarkdownCode          = 1UL << 4,    // fenced code blocks with language hint
    LinkInline            = 1UL << 5,    // [text](url) renders as a clickable link
    MarkdownStrikethrough = 1UL << 6,    // ~~text~~ — GFM, supported by most chat platforms
    MarkdownTaskList      = 1UL << 7,    // - [ ] / - [x] checkboxes — Markdig advanced + Teams

    // Rich rendering (bits 16–31)
    HtmlInline        = 1UL << 16,   // sanitized HTML inside markdown
    SvgInline         = 1UL << 17,   // inline <svg>
    ImageAttachment   = 1UL << 18,   // out-of-band image binaries

    // Platform-native UI primitives, reserved for future proxies (bits 32–47)
    DiscordEmbed      = 1UL << 32,
    SlackBlockKit     = 1UL << 33,
    TeamsAdaptiveCard = 1UL << 34,
}

/// <summary>
/// Conventional capability sets for each proxy implementation. Proxies declare their
/// preset on every outbound <see cref="UserMessage"/>; older proxies that don't set
/// the field default to <see cref="ClientCapabilities.None"/>, which falls through
/// to the agent's markdown-only behaviour.
/// </summary>
public static class ClientCapabilityPresets
{
    public const ClientCapabilities Cli =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode;

    public const ClientCapabilities Blazor =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode | ClientCapabilities.LinkInline |
        ClientCapabilities.MarkdownStrikethrough | ClientCapabilities.MarkdownTaskList |
        ClientCapabilities.HtmlInline | ClientCapabilities.SvgInline;

    // Documented in advance so capability-vocabulary decisions stay coherent —
    // not used by code until those proxies ship.
    public const ClientCapabilities WhatsApp =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic |
        ClientCapabilities.MarkdownStrikethrough | ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Discord =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.MarkdownStrikethrough |
        ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Slack =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.MarkdownStrikethrough |
        ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Teams =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode | ClientCapabilities.LinkInline |
        ClientCapabilities.MarkdownStrikethrough | ClientCapabilities.MarkdownTaskList |
        ClientCapabilities.ImageAttachment;
}
