using Microsoft.Playwright;

namespace RockBot.Browser;

/// <summary>
/// Owns the one Chromium process the service renders with. Launched on first use and
/// relaunched if it dies; each render gets its own throwaway context on top of it, so
/// cookies and storage never carry from one page to the next.
/// </summary>
internal sealed class BrowserHost(ILogger<BrowserHost> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true })
            return _browser;

        await _launchLock.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;

            if (_browser is not null)
            {
                logger.LogWarning("Chromium disconnected; relaunching");
                await _browser.DisposeAsync();
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                // Containers give /dev/shm 64MB by default, too little for Chromium.
                Args = ["--disable-dev-shm-usage"]
            });
            logger.LogInformation("Chromium {Version} launched", _browser.Version);
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        _launchLock.Dispose();
    }
}
