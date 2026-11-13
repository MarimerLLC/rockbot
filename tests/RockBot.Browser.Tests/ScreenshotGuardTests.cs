namespace RockBot.Browser.Tests;

/// <summary>
/// The checks that keep a screenshot from becoming a way to read private-network pages, tested
/// without a browser: the redirect hops the request guard never sees, and the shared time budget.
/// </summary>
[TestClass]
public class ScreenshotGuardTests
{
    private static readonly UrlGuard Guard = new(new BrowserOptions());

    private static Task<string?> FirstDisallowed(RedirectHops hops) =>
        hops.FirstDisallowedAsync(Guard, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

    [TestMethod]
    public async Task NoHops_NothingToRefuse()
    {
        Assert.IsNull(await FirstDisallowed(new RedirectHops()));
    }

    [TestMethod]
    [DataRow("http://169.254.169.254/latest/meta-data/")] // cloud metadata
    [DataRow("http://127.0.0.1:8082/attachments")]       // the agent's own endpoint
    [DataRow("http://localhost:8083/render")]
    [DataRow("http://10.0.0.12/admin")]
    [DataRow("http://[::1]/")]
    [DataRow("file:///etc/passwd")]
    [DataRow("not a url")]
    public async Task HopToAPrivateOrNonWebAddress_IsFound(string landed)
    {
        var hops = new RedirectHops();
        hops.Add("https://8.8.8.8/public-start");
        hops.Add(landed);

        Assert.AreEqual(landed, await FirstDisallowed(hops));
    }

    [TestMethod]
    public async Task HopsBetweenPublicAddresses_AreAllowed()
    {
        // IP literals keep the test off DNS. A CDN redirecting to a versioned URL is the normal case.
        var hops = new RedirectHops();
        hops.Add("https://8.8.8.8/npm/reveal.js@5.2.1/dist/reveal.js");
        hops.Add("https://1.1.1.1/font.woff2");

        Assert.IsNull(await FirstDisallowed(hops));
    }

    [TestMethod]
    public void RepeatedHop_IsRecordedOnce()
    {
        var hops = new RedirectHops();
        hops.Add("https://8.8.8.8/a");
        hops.Add("https://8.8.8.8/a");

        Assert.AreEqual(1, hops.Count);
    }

    [TestMethod]
    public void Budget_CountsDown_AndNeverReachesZero()
    {
        var budget = new PageScreenshotter.Budget(totalMs: 30_000);
        Assert.IsTrue(budget.Remaining() is > 29_000 and <= 30_000);

        // Playwright treats a timeout of 0 as "wait forever": a spent budget must stay a limit.
        var spent = new PageScreenshotter.Budget(totalMs: 0);
        Assert.AreEqual(PageScreenshotter.Budget.FloorMs, spent.Remaining());
    }
}
