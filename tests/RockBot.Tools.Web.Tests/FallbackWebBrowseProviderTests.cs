using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Tools.Web.Tests;

[TestClass]
public class FallbackWebBrowseProviderTests
{
    private const string ConsentShell = """
        <html><head><title>Schedule</title></head>
        <body><div id="CybotCookiebotDialog">This website uses cookies. Allow all. Deny.</div></body></html>
        """;

    private const string Url = "https://conf.example/speakers/jane";

    [TestMethod]
    public async Task Shell_IsRetriedInTheBrowser()
    {
        var render = new RenderStub(RenderJson(PageShellDetectorTests.ArticleWrappedPage, consentDismissed: true));
        var provider = MakeProvider(Html(ConsentShell), render, renderEnabled: true);

        var page = await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual(1, render.Calls);
        Assert.AreEqual(WebFetchStatus.Ok, page.Status);
        Assert.AreEqual(WebFetchMethod.Browser, page.FetchedVia);
        Assert.IsTrue(page.ConsentDismissed);
        StringAssert.Contains(page.Content, "Building Durable Agents");
    }

    [TestMethod]
    public async Task Shell_WithoutRenderService_ReturnsStatusAndNote()
    {
        var render = new RenderStub(RenderJson(PageShellDetectorTests.ArticleWrappedPage));
        var provider = MakeProvider(Html(ConsentShell), render, renderEnabled: false);

        var page = await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual(0, render.Calls);
        Assert.AreEqual(WebFetchStatus.BlockedByConsentWall, page.Status);
        Assert.AreEqual(FallbackWebBrowseProvider.RenderUnavailable, page.Note);
    }

    [TestMethod]
    public async Task OkPage_IsNotRendered()
    {
        var render = new RenderStub(RenderJson(ConsentShell));
        var provider = MakeProvider(Html(PageShellDetectorTests.ArticleWrappedPage), render, renderEnabled: true);

        var page = await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual(0, render.Calls);
        Assert.AreEqual(WebFetchStatus.Ok, page.Status);
        Assert.AreEqual(WebFetchMethod.Http, page.FetchedVia);
    }

    [TestMethod]
    public async Task ForceRender_RendersFirst()
    {
        var http = new CountingHandler(() => Html(PageShellDetectorTests.ArticleWrappedPage));
        var render = new RenderStub(RenderJson(PageShellDetectorTests.ArticleWrappedPage));
        var provider = MakeProvider(http, render, renderEnabled: true);

        var page = await provider.FetchAsync(Url, new WebFetchOptions { ForceRender = true }, CancellationToken.None);

        Assert.AreEqual(1, render.Calls);
        Assert.AreEqual(0, http.Calls);
        Assert.AreEqual(WebFetchMethod.Browser, page.FetchedVia);
    }

    [TestMethod]
    public async Task Forbidden_IsRetriedInTheBrowser()
    {
        var render = new RenderStub(RenderJson(PageShellDetectorTests.ArticleWrappedPage));
        var provider = MakeProvider(new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)), render, renderEnabled: true);

        var page = await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual(1, render.Calls);
        Assert.AreEqual(WebFetchStatus.Ok, page.Status);
    }

    [TestMethod]
    public async Task RenderFailure_KeepsHttpShellWithFailureNote()
    {
        var render = new RenderStub("""{"error":"navigation timed out"}""", HttpStatusCode.BadGateway);
        var provider = MakeProvider(Html(ConsentShell), render, renderEnabled: true);

        var page = await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual(WebFetchStatus.BlockedByConsentWall, page.Status);
        Assert.AreEqual(WebFetchMethod.Http, page.FetchedVia);
        StringAssert.Contains(page.Note, "navigation timed out");
    }

    [TestMethod]
    public async Task RenderService_PostsUrlToRenderEndpoint()
    {
        var render = new RenderStub(RenderJson(PageShellDetectorTests.ArticleWrappedPage));
        var provider = MakeProvider(Html(ConsentShell), render, renderEnabled: true);

        await provider.FetchAsync(Url, CancellationToken.None);

        Assert.AreEqual("http://browser.test/render", render.LastUri);
        using var body = JsonDocument.Parse(render.LastBody!);
        Assert.AreEqual(Url, body.RootElement.GetProperty("url").GetString());
    }

    private static FallbackWebBrowseProvider MakeProvider(HttpResponseMessage httpResponse, RenderStub render, bool renderEnabled)
        => MakeProvider(new CountingHandler(() => httpResponse), render, renderEnabled);

    private static FallbackWebBrowseProvider MakeProvider(HttpMessageHandler httpHandler, RenderStub render, bool renderEnabled)
    {
        var options = new WebToolOptions { RenderServiceUrl = renderEnabled ? "http://browser.test/" : null };
        var factory = new NamedFactory(httpHandler, render);
        return new FallbackWebBrowseProvider(
            new HttpWebBrowseProvider(factory, NullLogger<HttpWebBrowseProvider>.Instance, options),
            new RenderServiceWebBrowseProvider(factory, options, NullLogger<RenderServiceWebBrowseProvider>.Instance),
            NullLogger<FallbackWebBrowseProvider>.Instance);
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static string RenderJson(string html, bool consentDismissed = false) =>
        JsonSerializer.Serialize(new
        {
            finalUrl = Url,
            httpStatus = 200,
            title = "",
            html,
            consentDismissed,
            error = (string?)null
        });

    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private sealed class RenderStub(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NamedFactory(HttpMessageHandler http, HttpMessageHandler render) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(name == RenderServiceWebBrowseProvider.HttpClientName ? render : http, disposeHandler: false);
    }
}
