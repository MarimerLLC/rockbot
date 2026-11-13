using System.Collections.Concurrent;
using System.Diagnostics;
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
/// <para>
/// A picture is a stronger readout than the text <see cref="PageRenderer"/> returns: whatever a
/// frame or image shows ends up in it. The request guard does not see redirect hops, so every hop
/// is recorded and checked before the image leaves; if one landed on a private or local address
/// the capture is discarded.
/// </para>
/// </summary>
internal sealed class PageScreenshotter(
    GuardedContext contexts,
    UrlGuard guard,
    BrowserOptions options,
    ILogger<PageScreenshotter> logger)
{
    internal const string RedirectedToPrivateAddress =
        "The document followed a redirect to a private or local network address, so no screenshot is returned. " +
        "Load its resources from public URLs that do not redirect there.";

    public async Task<ScreenshotResult> CaptureAsync(ScreenshotRequest request, CancellationToken ct)
    {
        if (ScreenshotRequest.Validate(request) is { } invalid)
            return ScreenshotResult.Rejected(invalid);

        var (width, height) = ScreenshotRequest.Viewport(request);

        // One budget for the whole capture, the wait for a free page included. The caller's HTTP
        // timeout is sized from the number it sent, so the steps must share it, not each take it.
        var budget = new Budget(Math.Clamp(request.TimeoutMs ?? options.DefaultTimeoutMs, 1_000, options.MaxTimeoutMs));
        var dnsCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var redirectHops = new RedirectHops();
        var blocked = 0;

        IDisposable slot;
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waitCts.CancelAfter(budget.Remaining());
            try
            {
                slot = await contexts.EnterAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ScreenshotResult.Failed("The render service was busy for the whole time allowed. Try again.");
            }
        }

        using (slot)
        {
            try
            {
                // Fonts and images are the picture; let them load.
                await using var context = await contexts.OpenAsync(
                    width, height, budget.Remaining(), skipHeavyResources: false, dnsCache,
                    target =>
                    {
                        Interlocked.Increment(ref blocked);
                        logger.LogWarning("Blocked a request from a screenshot document to {Target}", target);
                    },
                    ct);

                // Fires for every request, redirect hops included, in every frame.
                context.Request += (_, req) =>
                {
                    if (req.RedirectedFrom is not null)
                        redirectHops.Add(req.Url);
                };

                var page = await context.NewPageAsync();
                await page.SetContentAsync(request.Html!, new PageSetContentOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = budget.Remaining()
                });

                await contexts.SettleAsync(page, LoadState.Load, budget.Remaining());
                await contexts.SettleAsync(page, LoadState.NetworkIdle, budget.Remaining());
                await WaitForFontsAsync(page, Math.Min(options.SettleMs, budget.Remaining()));

                var png = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false,
                    Timeout = budget.Remaining()
                });

                // Checked after the capture, so a hop that happened while it was taken counts too.
                if (await redirectHops.FirstDisallowedAsync(guard, dnsCache, ct) is { } leaked)
                {
                    logger.LogWarning("Discarded a screenshot: the document was redirected to {Target}", leaked);
                    return ScreenshotResult.Failed(RedirectedToPrivateAddress);
                }

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
    }

    // Text drawn before a web font arrives is laid out in the fallback face, which is exactly
    // the overflow a reviewer would then wrongly report. Best effort, like settling.
    private static async Task WaitForFontsAsync(IPage page, int maxMs)
    {
        try
        {
            await page.EvaluateAsync("() => document.fonts.ready.then(() => true)")
                .WaitAsync(TimeSpan.FromMilliseconds(maxMs));
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
        }
    }

    /// <summary>What is left of a fixed time allowance, never less than a token amount.</summary>
    internal sealed class Budget(int totalMs)
    {
        // Playwright reads a timeout of 0 as "no timeout"; a spent budget must still be a limit.
        internal const int FloorMs = 250;

        private readonly Stopwatch _elapsed = Stopwatch.StartNew();

        public int Remaining() => (int)Math.Max(FloorMs, totalMs - _elapsed.ElapsedMilliseconds);
    }
}

/// <summary>
/// The URLs a document's requests were redirected to. Playwright routes a request once, at its
/// first URL, and the browser follows any redirect without asking again, so these are the hops
/// the <see cref="UrlGuard"/> never saw.
/// </summary>
internal sealed class RedirectHops
{
    private readonly ConcurrentDictionary<string, byte> _urls = new(StringComparer.Ordinal);

    public void Add(string url) => _urls.TryAdd(url, 0);

    public int Count => _urls.Count;

    /// <summary>The first recorded hop the guard refuses, or null when all are allowed.</summary>
    public async Task<string?> FirstDisallowedAsync(UrlGuard guard, IDictionary<string, bool> dnsCache, CancellationToken ct)
    {
        foreach (var url in _urls.Keys)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !await guard.IsAllowedAsync(uri, dnsCache, ct))
                return url;
        }
        return null;
    }
}
