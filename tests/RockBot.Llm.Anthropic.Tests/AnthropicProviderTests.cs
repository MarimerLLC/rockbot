using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RockBot.Llm.Anthropic.Tests;

[TestClass]
public class AnthropicProviderTests
{
    private const string OkBody = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":2,"cache_creation_input_tokens":300,"cache_read_input_tokens":5000}}
        """;

    /// <summary>Records each request body and answers with a fixed status and body.</summary>
    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<Uri> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(JsonDocument.Parse(json).RootElement.Clone());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (IChatClient Client, CapturingHandler Handler) Build(
        AnthropicProviderOptions? options = null,
        string? effort = null,
        string? endpoint = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string body = OkBody)
    {
        var handler = new CapturingHandler(status, body);
        var client = AnthropicChatClientFactory.Create(
            "test-key", "claude-sonnet-5-5", endpoint, effort,
            options ?? new AnthropicProviderOptions(),
            sdkRetries: false, timeout: null, httpClient: new HttpClient(handler));
        return (client, handler);
    }

    private static readonly ChatMessage[] Hello = [new(ChatRole.System, "be brief"), new(ChatRole.User, "hello")];

    // ── request shape on the wire ────────────────────────────────────────────

    [TestMethod]
    public async Task Request_CarriesTopLevelCacheControl_ByDefault()
    {
        var (client, handler) = Build();

        await client.GetResponseAsync(Hello);

        var req = handler.Requests.Single();
        Assert.AreEqual("ephemeral", req.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.IsFalse(req.GetProperty("cache_control").TryGetProperty("ttl", out _),
            "Default TTL should be left to the API (5 minutes)");
    }

    [TestMethod]
    public async Task Request_CacheTtl1h_IsSent()
    {
        var (client, handler) = Build(new AnthropicProviderOptions { CacheTtl = "1h" });

        await client.GetResponseAsync(Hello);

        Assert.AreEqual("1h", handler.Requests.Single().GetProperty("cache_control").GetProperty("ttl").GetString());
    }

    [TestMethod]
    public async Task Request_PromptCachingOff_SendsNoCacheControl()
    {
        var (client, handler) = Build(new AnthropicProviderOptions { PromptCaching = false });

        await client.GetResponseAsync(Hello);

        Assert.IsFalse(handler.Requests.Single().TryGetProperty("cache_control", out _));
    }

    [TestMethod]
    public async Task Request_UsesConfiguredModelAndDefaultMaxTokens_AndKeepsSystemPrompt()
    {
        var (client, handler) = Build(new AnthropicProviderOptions { DefaultMaxOutputTokens = 12345 });

        await client.GetResponseAsync(Hello);

        var req = handler.Requests.Single();
        Assert.AreEqual("claude-sonnet-5-5", req.GetProperty("model").GetString());
        Assert.AreEqual(12345, req.GetProperty("max_tokens").GetInt32());
        StringAssert.Contains(req.GetProperty("system").GetRawText(), "be brief");
        Assert.AreEqual(1, req.GetProperty("messages").GetArrayLength());
    }

    [TestMethod]
    public async Task Request_HonoursPerCallMaxOutputTokensAndModelId()
    {
        var (client, handler) = Build();

        await client.GetResponseAsync(Hello, new ChatOptions { MaxOutputTokens = 77, ModelId = "claude-haiku-5-5" });

        var req = handler.Requests.Single();
        Assert.AreEqual(77, req.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("claude-haiku-5-5", req.GetProperty("model").GetString());
    }

    [TestMethod]
    public async Task Request_DropsSamplingParameters()
    {
        // Current Claude models 400 on any non-default temperature/top_p/top_k.
        var (client, handler) = Build();

        await client.GetResponseAsync(Hello, new ChatOptions { Temperature = 0.2f, TopP = 0.9f, TopK = 40 });

        var req = handler.Requests.Single();
        Assert.IsFalse(req.TryGetProperty("temperature", out _));
        Assert.IsFalse(req.TryGetProperty("top_p", out _));
        Assert.IsFalse(req.TryGetProperty("top_k", out _));
    }

    [TestMethod]
    public async Task Request_SendsReasoningEffort_AsOutputConfigEffort()
    {
        var (client, handler) = Build(effort: "minimal");

        await client.GetResponseAsync(Hello);

        Assert.AreEqual("low",
            handler.Requests.Single().GetProperty("output_config").GetProperty("effort").GetString());
    }

    [TestMethod]
    public async Task Request_EndpointWithV1Suffix_DoesNotDoubleTheVersion()
    {
        var (client, handler) = Build(endpoint: "https://api.anthropic.com/v1/");

        await client.GetResponseAsync(Hello);

        Assert.AreEqual("https://api.anthropic.com/v1/messages", handler.Uris.Single().GetLeftPart(UriPartial.Path));
    }

    [TestMethod]
    public async Task Request_SystemNudgeAfterAssistant_IsSentAsUser()
    {
        // The completion review appends a system message straight after the assistant's reply;
        // the API rejects role 'system' there ("must follow a 'user' message").
        var (client, handler) = Build();

        await client.GetResponseAsync(
        [
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi"),
            new(ChatRole.System, "review your answer"),
        ]);

        var messages = handler.Requests.Single().GetProperty("messages");
        Assert.AreEqual(3, messages.GetArrayLength());
        Assert.AreEqual("user", messages[2].GetProperty("role").GetString());
        StringAssert.Contains(messages[2].GetProperty("content").GetRawText(), "review your answer");
        StringAssert.Contains(handler.Requests.Single().GetProperty("system").GetRawText(), "be brief");
    }

    [TestMethod]
    public void PlaceSystemMessages_KeepsSystemAfterUserOrToolResult()
    {
        ChatMessage[] input =
        [
            new(ChatRole.System, "a"),
            new(ChatRole.User, "u"),
            new(ChatRole.System, "after user"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "f")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "r")]),
            new(ChatRole.System, "after tool"),
        ];

        var output = AnthropicRequestShapingChatClient.PlaceSystemMessages(input);

        Assert.AreSame(input, output, "Nothing to rewrite, so the original list is passed through");
    }

    [TestMethod]
    public void PlaceSystemMessages_RewritesEveryMessageInARunAfterAssistant()
    {
        ChatMessage[] input =
        [
            new(ChatRole.User, "u"),
            new(ChatRole.Assistant, "a"),
            new(ChatRole.System, "s1"),
            new(ChatRole.System, "s2"),
            new(ChatRole.User, "u2"),
            new(ChatRole.System, "s3"),
        ];

        var output = AnthropicRequestShapingChatClient.PlaceSystemMessages(input).ToList();

        CollectionAssert.AreEqual(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.System },
            output.Select(m => m.Role).ToArray());
        Assert.AreEqual("s1", output[2].Text);
        Assert.AreEqual(ChatRole.System, input[2].Role, "The caller's messages are not mutated");
    }

    // ── response mapping ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Response_ReportsCacheReadsAsCachedInputTokens()
    {
        var (client, _) = Build();

        var response = await client.GetResponseAsync(Hello);

        Assert.AreEqual("hi", response.Text);
        Assert.AreEqual(5000, response.Usage!.CachedInputTokenCount);
        Assert.AreEqual(5310, response.Usage.InputTokenCount, "input + cache writes + cache reads");
    }

    // ── error translation ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Error_OutOfCredits_SurfacesAsClientResultExceptionWithBody()
    {
        // FallbackChatClient classifies ClientResultException and matches "credit balance" in
        // the message, so the response body has to survive the translation.
        const string body = """
            {"type":"error","error":{"type":"invalid_request_error",
             "message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}
            """;
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: body);

        var ex = await Assert.ThrowsExactlyAsync<ClientResultException>(() => client.GetResponseAsync(Hello));

        Assert.AreEqual(400, ex.Status);
        StringAssert.Contains(ex.Message, "credit balance");
    }

    [TestMethod]
    public async Task Error_PromptTooLong_KeepsTheTokenCountsInTheMessage()
    {
        const string body = """
            {"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 208310 tokens > 200000 maximum"}}
            """;
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: body);

        var ex = await Assert.ThrowsExactlyAsync<ClientResultException>(() => client.GetResponseAsync(Hello));

        Assert.AreEqual(400, ex.Status);
        StringAssert.Contains(ex.Message, "prompt is too long: 208310 tokens > 200000 maximum");
    }

    // ── option parsing ───────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow("minimal", true)]
    [DataRow("HIGH", true)]
    [DataRow("xhigh", true)]
    [DataRow("max", true)]
    [DataRow("none", false)]
    [DataRow("turbo", false)]
    public void TryParseEffort_AcceptsClaudeLevels(string? value, bool ok)
    {
        Assert.AreEqual(ok, AnthropicChatClientFactory.TryParseEffort(value, out _));
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("https://api.anthropic.com", "https://api.anthropic.com")]
    [DataRow("https://api.anthropic.com/", "https://api.anthropic.com")]
    [DataRow("https://api.anthropic.com/v1/", "https://api.anthropic.com")]
    [DataRow("https://proxy.example/anthropic/v1", "https://proxy.example/anthropic")]
    public void NormaliseEndpoint_StripsTheVersionSegment(string? input, string? expected)
    {
        Assert.AreEqual(expected, AnthropicChatClientFactory.NormaliseEndpoint(input));
    }
}
