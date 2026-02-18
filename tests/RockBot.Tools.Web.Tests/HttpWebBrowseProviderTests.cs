using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Tools.Web.Tests;

[TestClass]
public class HttpWebBrowseProviderTests
{
    private const string SampleHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <title>Test Page Title</title>
            <style>body { color: red; }</style>
        </head>
        <body>
            <script>alert('hello');</script>
            <h1>Main Heading</h1>
            <p>This is a paragraph with a <a href="https://example.com">link</a>.</p>
        </body>
        </html>
        """;

    [TestMethod]
    public async Task FetchAsync_ExtractsTitleFromHtml()
    {
        var provider = MakeProvider(SampleHtml);

        var result = await provider.FetchAsync("https://example.com", CancellationToken.None);

        Assert.AreEqual("Test Page Title", result.Title);
    }

    [TestMethod]
    public async Task FetchAsync_ConvertsHtmlToMarkdown()
    {
        var provider = MakeProvider(SampleHtml);

        var result = await provider.FetchAsync("https://example.com", CancellationToken.None);

        StringAssert.Contains(result.Content, "Main Heading");
        StringAssert.Contains(result.Content, "paragraph");
    }

    [TestMethod]
    public async Task FetchAsync_StripsScriptTags()
    {
        var provider = MakeProvider(SampleHtml);

        var result = await provider.FetchAsync("https://example.com", CancellationToken.None);

        Assert.IsFalse(result.Content.Contains("alert('hello')"), "Script content should be stripped");
    }

    [TestMethod]
    public async Task FetchAsync_StripsStyleTags()
    {
        var provider = MakeProvider(SampleHtml);

        var result = await provider.FetchAsync("https://example.com", CancellationToken.None);

        Assert.IsFalse(result.Content.Contains("color: red"), "Style content should be stripped");
    }

    [TestMethod]
    public async Task FetchAsync_SetsSourceUrl()
    {
        var provider = MakeProvider("<html><head><title>T</title></head><body>Content</body></html>");

        var result = await provider.FetchAsync("https://example.com/page", CancellationToken.None);

        Assert.AreEqual("https://example.com/page", result.SourceUrl);
    }

    [TestMethod]
    public async Task FetchAsync_ConvertsLinksToMarkdown()
    {
        var provider = MakeProvider(SampleHtml);

        var result = await provider.FetchAsync("https://example.com", CancellationToken.None);

        StringAssert.Contains(result.Content, "[link](https://example.com)");
    }

    private static HttpWebBrowseProvider MakeProvider(string html)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
        return new HttpWebBrowseProvider(new StubFactory(new StubHandler(response)), NullLogger<HttpWebBrowseProvider>.Instance);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
