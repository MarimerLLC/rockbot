using RockBot.Host;

namespace RockBot.Tools.Web.Tests;

[TestClass]
public class WebBrowseToolExecutorTests
{
    private static readonly WebToolOptions DefaultOptions = new();

    private static ToolInvokeRequest MakeRequest(string? arguments = null, string? sessionId = null) => new()
    {
        ToolCallId = "call_1",
        ToolName = "web_browse",
        Arguments = arguments,
        SessionId = sessionId
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
        var executor = new WebBrowseToolExecutor(provider, null, DefaultOptions);

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
        var executor = new WebBrowseToolExecutor(provider, null, DefaultOptions);

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
        var executor = new WebBrowseToolExecutor(provider, null, DefaultOptions);

        var response = await executor.ExecuteAsync(MakeRequest("""{"url": "https://example.com"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual("Just the body content", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenProviderThrows()
    {
        var executor = new WebBrowseToolExecutor(
            new ThrowingBrowseProvider(new HttpRequestException("Connection refused")),
            null,
            DefaultOptions);

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
        var executor = new WebBrowseToolExecutor(provider, null, DefaultOptions);

        var response = await executor.ExecuteAsync(MakeRequest("""{"query": "wrong argument"}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "url");
    }

    [TestMethod]
    public async Task ExecuteAsync_LargeContent_WithSession_ReturnsChunkIndex()
    {
        var largeContent = new string('x', 10_000); // exceeds 8000 default threshold
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "Big Page",
            Content = largeContent,
            SourceUrl = "https://example.com/big"
        });
        var memory = new CapturingWorkingMemory();
        var options = new WebToolOptions { ChunkingThreshold = 8_000, ChunkMaxLength = 5_000, ChunkTtlMinutes = 20 };
        var executor = new WebBrowseToolExecutor(provider, memory, options);

        var response = await executor.ExecuteAsync(
            MakeRequest("""{"url": "https://example.com/big"}""", sessionId: "session-1"),
            CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsTrue(memory.Entries.Count > 0, "Expected chunks saved to working memory");
        StringAssert.Contains(response.Content, "chunk");
        StringAssert.Contains(response.Content, "GetFromWorkingMemory");
        Assert.IsTrue(memory.Entries.All(e => e.Key.StartsWith("web:")), "Keys should start with 'web:'");
        Assert.IsTrue(memory.Entries.All(e => e.Category == "web"), "Category should be 'web'");
    }

    [TestMethod]
    public async Task ExecuteAsync_LargeContent_NoSession_ReturnsTruncatedContent()
    {
        var largeContent = new string('x', 10_000); // exceeds 8000 default threshold
        var provider = new CapturingBrowseProvider(new WebPageContent
        {
            Title = "",
            Content = largeContent,
            SourceUrl = "https://example.com/big"
        });
        var executor = new WebBrowseToolExecutor(provider, null, DefaultOptions);

        // No session ID — should fall back to truncation
        var response = await executor.ExecuteAsync(
            MakeRequest("""{"url": "https://example.com/big"}""", sessionId: null),
            CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsNotNull(response.Content);
        StringAssert.Contains(response.Content, "truncated");
        Assert.IsTrue(response.Content!.Length < largeContent.Length, "Content should be truncated");
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

    private sealed class CapturingWorkingMemory : IWorkingMemory
    {
        public record EntryRecord(string SessionId, string Key, string Value, TimeSpan? Ttl, string? Category);
        public List<EntryRecord> Entries { get; } = [];

        public Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Entries.Add(new EntryRecord(sessionId, key, value, ttl, category));
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string sessionId, string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string sessionId, string key) => Task.CompletedTask;
        public Task ClearAsync(string sessionId) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
