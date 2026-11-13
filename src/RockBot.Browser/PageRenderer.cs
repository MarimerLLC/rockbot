using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// Loads one page in a fresh browser context, clicks away a consent banner if there is one,
/// waits for the page to settle, and returns the rendered DOM.
/// </summary>
internal sealed class PageRenderer(
    GuardedContext contexts,
    UrlGuard guard,
    BrowserOptions options,
    ILogger<PageRenderer> logger)
{
    public async Task<RenderResponse> RenderAsync(RenderRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return RenderResponse.Failed("Only absolute http(s) URLs can be rendered.");

        var dnsCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!await guard.IsAllowedAsync(uri, dnsCache, ct))
            return RenderResponse.Failed("The URL points at a private or local network address.");

        var timeout = Math.Clamp(request.TimeoutMs ?? options.DefaultTimeoutMs, 1_000, options.MaxTimeoutMs);

        using var slot = await contexts.EnterAsync(ct);
        try
        {
            // Text is all we return; skip the heavy bytes.
            await using var context = await contexts.OpenAsync(
                width: 1280, height: 1600, timeout, skipHeavyResources: true, dnsCache,
                blocked => logger.LogWarning("Blocked a request from {Page} to {Target}", uri.Host, blocked),
                ct);

            var page = await context.NewPageAsync();
            var response = await page.GotoAsync(uri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeout
            });

            // A redirect hop is not routed through the guard; check where the page ended up.
            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var landed) &&
                !await guard.IsAllowedAsync(landed, dnsCache, ct))
                return RenderResponse.Failed("The page redirected to a private or local network address.");

            await contexts.SettleAsync(page, LoadState.Load);
            var dismissed = await ConsentDismisser.TryDismissAsync(page);
            await contexts.SettleAsync(page, LoadState.NetworkIdle);

            var html = await page.ContentAsync();
            var title = await page.TitleAsync();

            logger.LogInformation(
                "Rendered {Url} → {Status}, {Length} chars of HTML, consent dismissed {Dismissed}",
                uri, response?.Status, html.Length, dismissed);

            return new RenderResponse(page.Url, response?.Status ?? 0, title, html, dismissed, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Playwright errors carry a call log after the first line; the first line is the story.
            var message = ex.Message.Split('\n', 2)[0].Trim();
            logger.LogWarning(ex is PlaywrightException ? null : ex, "Rendering {Url} failed: {Message}", uri, message);
            return RenderResponse.Failed(message);
        }
    }
}
