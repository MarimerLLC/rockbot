namespace RockBot.Tools.Web.Tests;

[TestClass]
public class WebBrowseToolExecutorTests
{
    private static ToolInvokeRequest MakeRequest(string? arguments = null) => new()
    {
        ToolCallId = "call_1",
        ToolName = "web_browse",
        Arguments = arguments
    };

    [TestMethod]
    public async Task ExecuteAsync_PassesUrlToProvider()
    {
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "Test Page",
            Content = "Content here",
            SourceUrl = "https://example.com"
        });
        var executor = new WebBrowseToolExecutor(provider);

        await executor.ExecuteAsync(MakeRequest("""{"url": "https://example.com"}"""), CancellationToken.None);

        Assert.AreEqual("https://example.com", provider.LastUrl);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsContentWithTitle()
    {
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "Test Page",
            Content = "Content here",
            SourceUrl = "https://example.com"
        });
        var executor = new WebBrowseToolExecutor(provider);

        var response = await executor.ExecuteAsync(MakeRequest("""{"url": "https://example.com"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsNotNull(response.Content);
        StringAssert.Contains(response.Content, "# Test Page");
        StringAssert.Contains(response.Content, "Content here");
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsContentWithoutTitle_WhenTitleEmpty()
    {
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "",
            Content = "Just the body content",
            SourceUrl = "https://example.com"
        });
        var executor = new WebBrowseToolExecutor(provider);

        var response = await executor.ExecuteAsync(MakeRequest("""{"url": "https://example.com"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual("Just the body content", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenProviderThrows()
    {
        var executor = new WebBrowseToolExecutor(
            new ThrowingBrowseProvider(new HttpRequestException("Connection refused")));

        var response = await executor.ExecuteAsync(MakeRequest("""{"url": "https://example.com"}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Failed to fetch page");
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenUrlMissing()
    {
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "",
            Content = "",
            SourceUrl = ""
        });
        var executor = new WebBrowseToolExecutor(provider);

        var response = await executor.ExecuteAsync(MakeRequest("""{"query": "wrong argument"}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "url");
    }

    private sealed class CapturingBrowseProvider(WebPageContent result) : IWebBrowseProvider
    {
        public string? LastUrl { get; private set; }

        public Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
        {
            LastUrl = url;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingBrowseProvider(Exception exception) : IWebBrowseProvider
    {
        public Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
            => Task.FromException<WebPageContent>(exception);
    }
}
