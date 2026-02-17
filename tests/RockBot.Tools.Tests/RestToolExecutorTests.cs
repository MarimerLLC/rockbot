using System.Net;
using System.Text.Json;
using RockBot.Tools.Rest;

namespace RockBot.Tools.Tests;

[TestClass]
public class RestToolExecutorTests
{
    [TestMethod]
    public void ExpandUrlTemplate_ReplacesPlaceholders()
    {
        var template = "https://api.example.com/weather?city={city}&units={units}";
        var args = new Dictionary<string, string>
        {
            ["city"] = "Seattle",
            ["units"] = "metric"
        };

        var result = RestToolExecutor.ExpandUrlTemplate(template, args);

        Assert.AreEqual("https://api.example.com/weather?city=Seattle&units=metric", result);
    }

    [TestMethod]
    public void ExpandUrlTemplate_UrlEncodesValues()
    {
        var template = "https://api.example.com/search?q={query}";
        var args = new Dictionary<string, string>
        {
            ["query"] = "hello world"
        };

        var result = RestToolExecutor.ExpandUrlTemplate(template, args);

        Assert.AreEqual("https://api.example.com/search?q=hello%20world", result);
    }

    [TestMethod]
    public void ExpandUrlTemplate_LeavesUnmatchedPlaceholders()
    {
        var template = "https://api.example.com/{resource}/{id}";
        var args = new Dictionary<string, string>
        {
            ["resource"] = "users"
        };

        var result = RestToolExecutor.ExpandUrlTemplate(template, args);

        Assert.AreEqual("https://api.example.com/users/{id}", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_SendsGetRequest()
    {
        var handler = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"temp": 72}""")
        });
        var factory = new StubHttpClientFactory(handler);

        var endpoint = new RestEndpointDefinition
        {
            Name = "get_weather",
            Description = "Gets weather",
            UrlTemplate = "https://api.example.com/weather?city={city}",
            Method = "GET"
        };

        var executor = new RestToolExecutor(endpoint, factory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "get_weather",
            Arguments = """{"city": "Seattle"}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("call_1", response.ToolCallId);
        Assert.AreEqual("get_weather", response.ToolName);
        Assert.AreEqual("""{"temp": 72}""", response.Content);
        Assert.IsFalse(response.IsError);

        Assert.AreEqual(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.AreEqual("https://api.example.com/weather?city=Seattle", handler.LastRequest.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsErrorOnFailedStatus()
    {
        var handler = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server Error")
        });
        var factory = new StubHttpClientFactory(handler);

        var endpoint = new RestEndpointDefinition
        {
            Name = "fail_tool",
            Description = "Fails",
            UrlTemplate = "https://api.example.com/fail"
        };

        var executor = new RestToolExecutor(endpoint, factory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "fail_tool"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("Server Error", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_SendsPostWithJsonBody()
    {
        var handler = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok")
        });
        var factory = new StubHttpClientFactory(handler);

        var endpoint = new RestEndpointDefinition
        {
            Name = "create_item",
            Description = "Creates an item",
            UrlTemplate = "https://api.example.com/items",
            Method = "POST",
            SendBodyAsJson = true
        };

        var executor = new RestToolExecutor(endpoint, factory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "create_item",
            Arguments = """{"name": "test"}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.IsNotNull(handler.LastRequest.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_AppliesBearerAuth()
    {
        Environment.SetEnvironmentVariable("TEST_BEARER_TOKEN", "my-secret-token");
        try
        {
            var handler = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
            var factory = new StubHttpClientFactory(handler);

            var endpoint = new RestEndpointDefinition
            {
                Name = "auth_tool",
                Description = "Authed tool",
                UrlTemplate = "https://api.example.com/data",
                AuthType = "bearer",
                AuthEnvVar = "TEST_BEARER_TOKEN"
            };

            var executor = new RestToolExecutor(endpoint, factory);
            var request = new ToolInvokeRequest
            {
                ToolCallId = "call_1",
                ToolName = "auth_tool"
            };

            await executor.ExecuteAsync(request, CancellationToken.None);

            Assert.AreEqual("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
            Assert.AreEqual("my-secret-token", handler.LastRequest.Headers.Authorization?.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_BEARER_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_AppliesApiKeyAuth()
    {
        Environment.SetEnvironmentVariable("TEST_API_KEY", "key-123");
        try
        {
            var handler = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
            var factory = new StubHttpClientFactory(handler);

            var endpoint = new RestEndpointDefinition
            {
                Name = "apikey_tool",
                Description = "API key tool",
                UrlTemplate = "https://api.example.com/data",
                AuthType = "api_key",
                AuthEnvVar = "TEST_API_KEY",
                ApiKeyHeader = "X-Custom-Key"
            };

            var executor = new RestToolExecutor(endpoint, factory);
            var request = new ToolInvokeRequest
            {
                ToolCallId = "call_1",
                ToolName = "apikey_tool"
            };

            await executor.ExecuteAsync(request, CancellationToken.None);

            Assert.IsTrue(handler.LastRequest!.Headers.Contains("X-Custom-Key"));
            Assert.AreEqual("key-123", handler.LastRequest.Headers.GetValues("X-Custom-Key").First());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY", null);
        }
    }
}

/// <summary>
/// Stub HttpMessageHandler for testing.
/// </summary>
internal sealed class StubHttpHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// Stub IHttpClientFactory for testing.
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}
