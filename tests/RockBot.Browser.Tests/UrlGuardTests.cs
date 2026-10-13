using System.Net;

namespace RockBot.Browser.Tests;

[TestClass]
public class UrlGuardTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.1.2.3")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.1.1")]
    [DataRow("169.254.169.254")]
    [DataRow("100.64.0.1")]
    [DataRow("0.0.0.0")]
    [DataRow("::1")]
    [DataRow("fe80::1")]
    [DataRow("fd00::1")]
    [DataRow("::ffff:10.0.0.1")]
    public void IsPublic_RejectsLocalAndPrivateAddresses(string address)
    {
        Assert.IsFalse(UrlGuard.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("172.32.0.1")]
    [DataRow("100.128.0.1")]
    [DataRow("2606:4700:4700::1111")]
    public void IsPublic_AcceptsPublicAddresses(string address)
    {
        Assert.IsTrue(UrlGuard.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    [DataRow("http://localhost:8081/health")]
    [DataRow("http://127.0.0.1:8082/upload")]
    [DataRow("http://[::1]/")]
    [DataRow("http://169.254.169.254/latest/meta-data")]
    [DataRow("file:///etc/passwd")]
    [DataRow("ftp://example.com/file")]
    public async Task IsAllowed_BlocksLocalTargetsAndOtherSchemes(string url)
    {
        var guard = new UrlGuard(new BrowserOptions());

        Assert.IsFalse(await guard.IsAllowedAsync(new Uri(url), new Dictionary<string, bool>(), CancellationToken.None));
    }

    [TestMethod]
    public async Task IsAllowed_PassesBrowserInternalSchemes()
    {
        var guard = new UrlGuard(new BrowserOptions());

        Assert.IsTrue(await guard.IsAllowedAsync(new Uri("data:text/plain,hi"), new Dictionary<string, bool>(), CancellationToken.None));
    }

    [TestMethod]
    public async Task IsAllowed_AllowPrivateNetworks_LetsLocalThrough()
    {
        var guard = new UrlGuard(new BrowserOptions { AllowPrivateNetworks = true });

        Assert.IsTrue(await guard.IsAllowedAsync(new Uri("http://localhost:5000/"), new Dictionary<string, bool>(), CancellationToken.None));
    }

    [TestMethod]
    public async Task IsAllowed_CachesVerdictPerHost()
    {
        var guard = new UrlGuard(new BrowserOptions());
        var cache = new Dictionary<string, bool>();

        await guard.IsAllowedAsync(new Uri("http://10.0.0.5/a"), cache, CancellationToken.None);

        Assert.IsTrue(cache.TryGetValue("10.0.0.5", out var verdict));
        Assert.IsFalse(verdict);
    }
}
