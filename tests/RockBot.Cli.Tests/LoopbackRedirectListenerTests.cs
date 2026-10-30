using System.Net;
using RockBot.UserProxy.Cli.Auth;

namespace RockBot.Cli.Tests;

/// <summary>The CLI's redirect URI for MCP OAuth sign-in (#738).</summary>
[TestClass]
public class LoopbackRedirectListenerTests
{
    [TestMethod]
    public void ParseRedirect_ReadsCodeStateAndIssuer()
    {
        var callback = LoopbackRedirectListener.ParseRedirect(
            " http://127.0.0.1:8080/callback?code=abc&state=s%2B1&iss=https%3A%2F%2Fas.example ");

        Assert.AreEqual("abc", callback!.Code);
        Assert.AreEqual("s+1", callback.State);
        Assert.AreEqual("https://as.example", callback.Iss);
        Assert.IsNull(callback.Error);
    }

    [TestMethod]
    public void ParseRedirect_ReadsARefusal()
    {
        var callback = LoopbackRedirectListener.ParseRedirect(
            "http://127.0.0.1:8080/callback?error=access_denied&error_description=nope&state=s");

        Assert.AreEqual("access_denied", callback!.Error);
        Assert.AreEqual("nope", callback.ErrorDescription);
        Assert.IsNull(callback.Code);
    }

    [TestMethod]
    public void ParseRedirect_RejectsAnythingThatIsNotARedirect()
    {
        Assert.IsNull(LoopbackRedirectListener.ParseRedirect("not a url"));
        Assert.IsNull(LoopbackRedirectListener.ParseRedirect("http://127.0.0.1:8080/callback?code=abc"));
    }

    [TestMethod]
    public async Task Listener_ReceivesTheBrowsersRedirect_AndSkipsOtherRequests()
    {
        using var listener = LoopbackRedirectListener.Start();
        StringAssert.StartsWith(listener.RedirectUri, "http://127.0.0.1:");
        StringAssert.EndsWith(listener.RedirectUri, "/callback");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = listener.WaitAsync(cts.Token);

        using var browser = new HttpClient();
        var origin = new Uri(listener.RedirectUri).GetLeftPart(UriPartial.Authority);
        var favicon = await browser.GetAsync($"{origin}/favicon.ico", cts.Token);
        Assert.AreEqual(HttpStatusCode.NotFound, favicon.StatusCode);

        var page = await browser.GetAsync($"{listener.RedirectUri}?code=abc&state=st", cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(await page.Content.ReadAsStringAsync(cts.Token), "back to the terminal");

        var callback = await wait;
        Assert.AreEqual("abc", callback.Code);
        Assert.AreEqual("st", callback.State);
    }
}
