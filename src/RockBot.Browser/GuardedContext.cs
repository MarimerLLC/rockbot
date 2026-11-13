using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// What <see cref="PageRenderer"/> and <see cref="PageScreenshotter"/> share: the limit on pages
/// open at once, and a throwaway browser context whose every request passes the
/// <see cref="UrlGuard"/>.
/// </summary>
internal sealed class GuardedContext(BrowserHost host, UrlGuard guard, BrowserOptions options)
{
    // The default headless user agent says "HeadlessChrome", which many sites refuse outright.
    // Matches the agent's plain HTTP client.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // One gate for renders and screenshots together: each open page holds a Chromium renderer.
    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.MaxConcurrency));

    /// <summary>Waits for a free page slot. Dispose the result to give it back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Slot(_gate);
    }

    /// <summary>
    /// Opens a fresh context, so cookies and storage never carry from one page to the next, and
    /// routes its requests through the guard.
    /// </summary>
    /// <param name="skipHeavyResources">
    /// Drops image, media and font requests. Right when only the text is wanted; wrong for a picture.
    /// </param>
    /// <param name="onBlocked">Called with the URL of each request the guard refused.</param>
    public async Task<IBrowserContext> OpenAsync(
        int width,
        int height,
        int timeoutMs,
        bool skipHeavyResources,
        ConcurrentDictionary<string, bool> dnsCache,
        Action<string> onBlocked,
        CancellationToken ct)
    {
        var browser = await host.GetBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false
        });
        context.SetDefaultTimeout(timeoutMs);

        await context.RouteAsync("**/*", async route =>
        {
            var req = route.Request;

            if (skipHeavyResources && req.ResourceType is "image" or "media" or "font")
            {
                await route.AbortAsync();
                return;
            }

            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var target) ||
                !await guard.IsAllowedAsync(target, dnsCache, CancellationToken.None))
            {
                onBlocked(req.Url);
                await route.AbortAsync("blockedbyclient");
                return;
            }

            await route.ContinueAsync();
        });

        return context;
    }

    /// <summary>Best effort: pages that keep polling never go idle, and are taken as they stand.</summary>
    public async Task SettleAsync(IPage page, LoadState state)
    {
        try
        {
            await page.WaitForLoadStateAsync(state, new PageWaitForLoadStateOptions { Timeout = options.SettleMs });
        }
        catch (PlaywrightException)
        {
        }
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
