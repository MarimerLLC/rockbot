using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// Loads an HTML document the caller supplies — a slide deck or report page the agent wrote —
/// in a fresh browser context and returns a PNG of the viewport, so the agent can look at what
/// it produced before showing it to anyone (#669).
/// <para>
/// The document arrives as text: this service does not mount the agent's files. Its scripts run,
/// because that is how a deck draws its first slide, under the same confinement as any page
/// <see cref="PageRenderer"/> loads: no cookies or storage, and no request to a private or
/// local address.
/// </para>
/// </summary>
internal sealed class PageScreenshotter(
    GuardedContext contexts,
    BrowserOptions options,
    ILogger<PageScreenshotter> logger)
{
    public async Task<ScreenshotResult> CaptureAsync(ScreenshotRequest request, CancellationToken ct)
    {
        if (ScreenshotRequest.Validate(request) is { } invalid)
            return ScreenshotResult.Rejected(invalid);

        var (width, height) = ScreenshotRequest.Viewport(request);
        var timeout = Math.Clamp(request.TimeoutMs ?? options.DefaultTimeoutMs, 1_000, options.MaxTimeoutMs);
        var dnsCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var blocked = 0;

        using var slot = await contexts.EnterAsync(ct);
        try
        {
            // Fonts and images are the picture; let them load.
            await using var context = await contexts.OpenAsync(
                width, height, timeout, skipHeavyResources: false, dnsCache,
                target =>
                {
                    Interlocked.Increment(ref blocked);
                    logger.LogWarning("Blocked a request from a screenshot document to {Target}", target);
                },
                ct);

            var page = await context.NewPageAsync();
            await page.SetContentAsync(request.Html!, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeout
            });

            await contexts.SettleAsync(page, LoadState.Load);
            await contexts.SettleAsync(page, LoadState.NetworkIdle);
            await WaitForFontsAsync(page);

            var png = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false,
                Timeout = timeout
            });

            logger.LogInformation(
                "Captured a {Width}x{Height} screenshot of a {Length}-char document ({Bytes} bytes, {Blocked} requests blocked)",
                width, height, request.Html!.Length, png.Length, blocked);

            return ScreenshotResult.Captured(png, blocked);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Playwright errors carry a call log after the first line; the first line is the story.
            var message = ex.Message.Split('\n', 2)[0].Trim();
            logger.LogWarning(ex is PlaywrightException ? null : ex, "Screenshot failed: {Message}", message);
            return ScreenshotResult.Failed(message);
        }
    }

    // Text drawn before a web font arrives is laid out in the fallback face, which is exactly
    // the overflow a reviewer would then wrongly report. Best effort, like settling.
    private async Task WaitForFontsAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync("() => document.fonts.ready.then(() => true)")
                .WaitAsync(TimeSpan.FromMilliseconds(options.SettleMs));
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
        }
    }
}
