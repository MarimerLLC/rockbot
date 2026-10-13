using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// Loads one page in a fresh browser context, clicks away a consent banner if there is one,
/// waits for the page to settle, and returns the rendered DOM.
/// </summary>
internal sealed class PageRenderer(
    BrowserHost host,
    UrlGuard guard,
    BrowserOptions options,
    ILogger<PageRenderer> logger)
{
    // The default headless user agent says "HeadlessChrome", which many sites refuse outright.
    // Matches the agent's plain HTTP client.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.MaxConcurrency));

    public async Task<RenderResponse> RenderAsync(RenderRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return RenderResponse.Failed("Only absolute http(s) URLs can be rendered.");

        var dnsCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!await guard.IsAllowedAsync(uri, dnsCache, ct))
            return RenderResponse.Failed("The URL points at a private or local network address.");

        var timeout = Math.Clamp(request.TimeoutMs ?? options.DefaultTimeoutMs, 1_000, options.MaxTimeoutMs);

        await _gate.WaitAsync(ct);
        try
        {
            var browser = await host.GetBrowserAsync(ct);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = UserAgent,
                Locale = "en-US",
                ViewportSize = new ViewportSize { Width = 1280, Height = 1600 },
                ServiceWorkers = ServiceWorkerPolicy.Block,
                AcceptDownloads = false
            });
            context.SetDefaultTimeout(timeout);

            await context.RouteAsync("**/*", async route =>
            {
                var req = route.Request;

                // Text is all we return; skip the heavy bytes.
                if (req.ResourceType is "image" or "media" or "font")
                {
                    await route.AbortAsync();
                    return;
                }

                if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var target) ||
                    !await guard.IsAllowedAsync(target, dnsCache, CancellationToken.None))
                {
                    logger.LogWarning("Blocked a request from {Page} to {Target}", uri.Host, req.Url);
                    await route.AbortAsync("blockedbyclient");
                    return;
                }

                await route.ContinueAsync();
            });

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

            await SettleAsync(page, LoadState.Load);
            var dismissed = await ConsentDismisser.TryDismissAsync(page);
            await SettleAsync(page, LoadState.NetworkIdle);

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
        finally
        {
            _gate.Release();
        }
    }

    // Best effort: pages that keep polling never go idle, and are read as they stand.
    private async Task SettleAsync(IPage page, LoadState state)
    {
        try
        {
            await page.WaitForLoadStateAsync(state, new PageWaitForLoadStateOptions { Timeout = options.SettleMs });
        }
        catch (PlaywrightException)
        {
        }
    }
}
