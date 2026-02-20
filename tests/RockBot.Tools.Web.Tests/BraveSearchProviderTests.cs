using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools.Web.Brave;

namespace RockBot.Tools.Web.Tests;

[TestClass]
public class BraveSearchProviderTests
{
    private const string ValidResponse = """
        {
          "web": {
            "results": [
              {
                "title": "Example Result",
                "url": "https://example.com",
                "description": "This is a snippet"
              },
              {
                "title": "Another Result",
                "url": "https://another.com",
                "description": "Another snippet"
              }
            ]
          }
        }
        """;

    [TestMethod]
    public async Task SearchAsync_SendsCorrectUrlAndHeaders()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidResponse, Encoding.UTF8, "application/json")
        });
        var options = new WebToolOptions { ApiKeyEnvVar = "ROCKBOT_TEST_BRAVE_KEY_URL" };
        Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_URL", "test-api-key");
        try
        {
            var provider = new BraveSearchProvider(new StubFactory(handler), options, NullLogger<BraveSearchProvider>.Instance);
            await provider.SearchAsync("hello world", 5, CancellationToken.None);

            Assert.IsNotNull(handler.CapturedRequest);
            StringAssert.Contains(handler.CapturedRequest.RequestUri!.AbsoluteUri, "q=hello%20world");
            StringAssert.Contains(handler.CapturedRequest.RequestUri.AbsoluteUri, "count=5");
            Assert.IsTrue(handler.CapturedRequest.Headers.Contains("X-Subscription-Token"));
            Assert.AreEqual("test-api-key",
                handler.CapturedRequest.Headers.GetValues("X-Subscription-Token").First());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_URL", null);
        }
    }

    [TestMethod]
    public async Task SearchAsync_DeserializesResultsCorrectly()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidResponse, Encoding.UTF8, "application/json")
        });
        var options = new WebToolOptions { ApiKeyEnvVar = "ROCKBOT_TEST_BRAVE_KEY_DESER" };
        Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_DESER", "test-api-key");
        try
        {
            var provider = new BraveSearchProvider(new StubFactory(handler), options, NullLogger<BraveSearchProvider>.Instance);
            var results = await provider.SearchAsync("test", 10, CancellationToken.None);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("Example Result", results[0].Title);
            Assert.AreEqual("https://example.com", results[0].Url);
            Assert.AreEqual("This is a snippet", results[0].Snippet);
            Assert.AreEqual("Another Result", results[1].Title);
            Assert.AreEqual("Another snippet", results[1].Snippet);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_DESER", null);
        }
    }

    [TestMethod]
    public async Task SearchAsync_ReturnsEmpty_WhenApiKeyMissing()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var options = new WebToolOptions { ApiKeyEnvVar = "ROCKBOT_TEST_BRAVE_KEY_NONEXISTENT_12345" };
        var provider = new BraveSearchProvider(new StubFactory(handler), options, NullLogger<BraveSearchProvider>.Instance);

        var results = await provider.SearchAsync("test", 10, CancellationToken.None);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_CapsCountAt20()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"web": {"results": []}}""", Encoding.UTF8, "application/json")
        });
        var options = new WebToolOptions { ApiKeyEnvVar = "ROCKBOT_TEST_BRAVE_KEY_CAP" };
        Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_CAP", "test-api-key");
        try
        {
            var provider = new BraveSearchProvider(new StubFactory(handler), options, NullLogger<BraveSearchProvider>.Instance);
            await provider.SearchAsync("test", 50, CancellationToken.None);

            StringAssert.Contains(handler.CapturedRequest!.RequestUri!.AbsoluteUri, "count=20");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROCKBOT_TEST_BRAVE_KEY_CAP", null);
        }
    }

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
