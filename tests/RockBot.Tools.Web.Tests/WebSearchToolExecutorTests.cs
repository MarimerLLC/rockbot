namespace RockBot.Tools.Web.Tests;

[TestClass]
public class WebSearchToolExecutorTests
{
    private static ToolInvokeRequest MakeRequest(string? arguments = null) => new()
    {
        ToolCallId = "call_1",
        ToolName = "web_search",
        Arguments = arguments
    };

    [TestMethod]
    public async Task ExecuteAsync_FormatsResultsAsNumberedMarkdownList()
    {
        var provider = new StubSearchProvider([
            new WebSearchResult { Title = "Result One", Url = "https://example.com/1", Snippet = "First result snippet" },
            new WebSearchResult { Title = "Result Two", Url = "https://example.com/2", Snippet = "Second result snippet" }
        ]);
        var executor = new WebSearchToolExecutor(provider, new WebToolOptions());

        var response = await executor.ExecuteAsync(MakeRequest("""{"query": "test query"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsNotNull(response.Content);
        StringAssert.Contains(response.Content, "1. [Result One](https://example.com/1)");
        StringAssert.Contains(response.Content, "   First result snippet");
        StringAssert.Contains(response.Content, "2. [Result Two](https://example.com/2)");
        StringAssert.Contains(response.Content, "   Second result snippet");
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsNoResultsMessage_WhenEmpty()
    {
        var executor = new WebSearchToolExecutor(new StubSearchProvider([]), new WebToolOptions());

        var response = await executor.ExecuteAsync(MakeRequest("""{"query": "obscure query"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual("No results found.", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenProviderThrows()
    {
        var executor = new WebSearchToolExecutor(
            new ThrowingSearchProvider(new HttpRequestException("Network error")),
            new WebToolOptions());

        var response = await executor.ExecuteAsync(MakeRequest("""{"query": "test"}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Search failed");
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenQueryMissing()
    {
        var executor = new WebSearchToolExecutor(new StubSearchProvider([]), new WebToolOptions());

        var response = await executor.ExecuteAsync(MakeRequest("""{"count": 5}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "query");
    }

    [TestMethod]
    public async Task ExecuteAsync_RespectsCountArgument()
    {
        var provider = new CountCapturingSearchProvider();
        var executor = new WebSearchToolExecutor(provider, new WebToolOptions { MaxSearchResults = 10 });

        await executor.ExecuteAsync(MakeRequest("""{"query": "test", "count": 5}"""), CancellationToken.None);

        Assert.AreEqual(5, provider.LastCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_CapsCountAt20()
    {
        var provider = new CountCapturingSearchProvider();
        var executor = new WebSearchToolExecutor(provider, new WebToolOptions());

        await executor.ExecuteAsync(MakeRequest("""{"query": "test", "count": 99}"""), CancellationToken.None);

        Assert.AreEqual(20, provider.LastCount);
    }

    private sealed class StubSearchProvider(IReadOnlyList<WebSearchResult> results) : IWebSearchProvider
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromResult(results);
    }

    private sealed class ThrowingSearchProvider(Exception exception) : IWebSearchProvider
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
            => Task.FromException<IReadOnlyList<WebSearchResult>>(exception);
    }

    private sealed class CountCapturingSearchProvider : IWebSearchProvider
    {
        public int LastCount { get; private set; }

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
        {
            LastCount = maxResults;
            return Task.FromResult<IReadOnlyList<WebSearchResult>>([]);
        }
    }
}
