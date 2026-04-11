using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace RockBot.A2A.Gateway.Tests;

[TestClass]
public class JsonRpcRouterTests
{
    /// <summary>
    /// POST / without API key should return 401 with JSON-RPC error body.
    /// </summary>
    [TestMethod]
    public async Task Post_WithoutApiKey_Returns401JsonRpcError()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var payload = JsonRpcRequest("SendMessage", new { message = new { role = "user", parts = new[] { new { text = "hello" } } } });
        var response = await client.PostAsync("/", payload);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("\"jsonrpc\""), $"Expected JSON-RPC envelope, got: {body}");
        Assert.IsTrue(body.Contains("Authentication required"), $"Expected auth error message, got: {body}");
    }

    /// <summary>
    /// GET /.well-known/agent-card.json should succeed without auth.
    /// </summary>
    [TestMethod]
    public async Task GetAgentCard_WithoutApiKey_Succeeds()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/agent-card.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("RockBot"), $"Expected agent name in card, got: {body}");
        Assert.IsTrue(body.Contains("apiKey"), $"Expected security scheme, got: {body}");
        Assert.IsTrue(body.Contains("X-Api-Key"), $"Expected header name in security scheme, got: {body}");
    }

    /// <summary>
    /// POST / with valid API key but unknown method should return -32601.
    /// </summary>
    [TestMethod]
    public async Task Post_UnknownMethod_ReturnsMethodNotFound()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var payload = JsonRpcRequest("NonExistentMethod", new { });
        var response = await client.PostAsync("/", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("-32601"), $"Expected method-not-found code, got: {body}");
        Assert.IsTrue(body.Contains("NonExistentMethod"), $"Expected method name in error, got: {body}");
    }

    /// <summary>
    /// POST / with malformed JSON should return parse error.
    /// </summary>
    [TestMethod]
    public async Task Post_MalformedJson_ReturnsParseError()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var content = new StringContent("not json", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/", content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("-32700"), $"Expected parse error code, got: {body}");
    }

    /// <summary>
    /// POST / with missing params should return invalid request.
    /// </summary>
    [TestMethod]
    public async Task Post_MissingParams_ReturnsInvalidRequest()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var json = """{"jsonrpc":"2.0","id":1,"method":"SendMessage"}""";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/", content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("-32600"), $"Expected invalid-request code, got: {body}");
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    // Override with test API key and disable RabbitMQ connection
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ApiKeys:test-key:AgentId"] = "test-agent",
                        ["ApiKeys:test-key:DisplayName"] = "Test Agent",
                        ["RabbitMq:HostName"] = "localhost",
                        ["RabbitMq:Port"] = "5672",
                        ["RabbitMq:UserName"] = "guest",
                        ["RabbitMq:Password"] = "guest"
                    });
                });
            });
    }

    private static StringContent JsonRpcRequest(string method, object @params)
    {
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params
        });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
