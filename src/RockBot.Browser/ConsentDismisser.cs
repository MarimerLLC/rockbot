using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// Clicks away a cookie-consent banner so the page underneath can be read. Refusing is
/// preferred over accepting: the render context is thrown away after one page, so the
/// choice never persists, and refusing loads fewer trackers. Accept is the fallback for
/// banners that offer nothing else.
/// </summary>
internal static class ConsentDismisser
{
    // Known consent platforms, refuse buttons first.
    private static readonly string[] RejectSelectors =
    [
        "#onetrust-reject-all-handler",
        "#CybotCookiebotDialogBodyButtonDecline",
        "#didomi-notice-disagree-button",
        ".qc-cmp2-summary-buttons button[mode=secondary]",
        "button.sp_choice_type_13",
        "#cookie-reject"
    ];

    private static readonly string[] AcceptSelectors =
    [
        "#onetrust-accept-btn-handler",
        "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll",
        "#CybotCookiebotDialogBodyButtonAccept",
        "#didomi-notice-agree-button",
        ".qc-cmp2-summary-buttons button[mode=primary]",
        "button.sp_choice_type_11",
        "#cookie-accept"
    ];

    // Home-grown banners: match the button's accessible name, English, Dutch, German, French.
    private static readonly Regex RejectName = new(
        @"^\s*(reject all|reject|decline all|decline|deny all|deny|refuse all|only necessary|only essential|necessary only|essential only|use necessary cookies only|alles weigeren|weigeren|alleen noodzakelijke cookies|alleen noodzakelijk|alle ablehnen|ablehnen|nur notwendige cookies|nur notwendige|tout refuser|refuser)\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex AcceptName = new(
        @"^\s*(accept all cookies|accept all|accept cookies|accept|allow all cookies|allow all|i accept|i agree|agree|got it|alles accepteren|accepteren|akkoord|alle akzeptieren|akzeptieren|zustimmen|tout accepter|accepter)\s*$",
        RegexOptions.IgnoreCase);

    private const float ClickTimeoutMs = 2_000;

    /// <summary>
    /// Tries the refuse buttons, then the accept buttons, in the page and in its frames
    /// (some platforms render the banner in an iframe). Returns true after the first click.
    /// </summary>
    public static async Task<bool> TryDismissAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            if (await TryClickAnyAsync(frame, RejectSelectors, RejectName))
                return true;
        }

        foreach (var frame in page.Frames)
        {
            if (await TryClickAnyAsync(frame, AcceptSelectors, AcceptName))
                return true;
        }

        return false;
    }

    private static async Task<bool> TryClickAnyAsync(IFrame frame, string[] selectors, Regex name)
    {
        foreach (var selector in selectors)
        {
            if (await TryClickAsync(frame.Locator(selector).First))
                return true;
        }

        if (await TryClickAsync(frame.GetByRole(AriaRole.Button, new FrameGetByRoleOptions { NameRegex = name }).First))
            return true;

        return await TryClickAsync(frame.GetByRole(AriaRole.Link, new FrameGetByRoleOptions { NameRegex = name }).First);
    }

    private static async Task<bool> TryClickAsync(ILocator locator)
    {
        try
        {
            if (!await locator.IsVisibleAsync())
                return false;
            await locator.ClickAsync(new LocatorClickOptions { Timeout = ClickTimeoutMs });
            return true;
        }
        catch (PlaywrightException)
        {
            // Detached frame, covered element, navigation mid-click: try the next candidate.
            return false;
        }
    }
}
