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
    public async Task Request_MarksTheLastSystemBlockForCaching()
    {
        // Second breakpoint: keeps tools + system prompt cached when earlier history is rewritten.
        var (client, handler) = Build(new AnthropicProviderOptions { CacheTtl = "1h" });
        ChatMessage[] input =
        [
            new(ChatRole.System, "first"),
            new(ChatRole.System, "second"),
            new(ChatRole.User, "hello"),
        ];

        await client.GetResponseAsync(input);

        var system = handler.Requests.Single().GetProperty("system");
        Assert.AreEqual(2, system.GetArrayLength());
        Assert.IsFalse(system[0].TryGetProperty("cache_control", out _));
        Assert.AreEqual("ephemeral", system[1].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.AreEqual("1h", system[1].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.IsNull(input[1].Contents[0].AdditionalProperties, "The caller's message is not mutated");
    }

    [TestMethod]
    public async Task Request_PromptCachingOff_MarksNoSystemBlock()
    {
        var (client, handler) = Build(new AnthropicProviderOptions { PromptCaching = false });

        await client.GetResponseAsync(Hello);

        StringAssert.DoesNotMatch(handler.Requests.Single().GetRawText(),
            new System.Text.RegularExpressions.Regex("cache_control"));
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
    public void PlaceSystemMessages_SendsEveryNonLeadingSystemMessageAsUser()
    {
        // The API also rejects a system message that is followed by a user/tool message
        // ("must precede an 'assistant' message or end the array"), so placement can't be
        // judged per position; all mid-conversation system messages become user messages.
        ChatMessage[] input =
        [
            new(ChatRole.System, "a"),
            new(ChatRole.System, "b"),
            new(ChatRole.User, "u"),
            new(ChatRole.System, "after user"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "f")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "r")]),
            new(ChatRole.System, "after tool"),
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a"),
            new(ChatRole.System, "s1"),
            new(ChatRole.System, "s2"),
        ];

        var output = AnthropicRequestShapingChatClient.PlaceSystemMessages(input).ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                ChatRole.System, ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant,
                ChatRole.Tool, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User,
            },
            output.Select(m => m.Role).ToArray());
        Assert.AreEqual("after tool", output[6].Text);
        Assert.AreEqual(ChatRole.System, input[3].Role, "The caller's messages are not mutated");
    }

    [TestMethod]
    public void PlaceSystemMessages_LeadingOnly_PassesTheListThrough()
    {
        ChatMessage[] input = [new(ChatRole.System, "a"), new(ChatRole.User, "u"), new(ChatRole.Assistant, "x")];

        Assert.AreSame(input, AnthropicRequestShapingChatClient.PlaceSystemMessages(input));
    }

    [TestMethod]
    public async Task Request_SystemBeforeUser_IsSentAsUser()
    {
        // messages.19 in the live failure: a system message followed by more user content.
        var (client, handler) = Build();

        await client.GetResponseAsync(
        [
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "hello"),
            new(ChatRole.System, "nudge"),
            new(ChatRole.User, "more"),
        ]);

        var raw = handler.Requests.Single().GetProperty("messages").GetRawText();
        StringAssert.DoesNotMatch(raw, new System.Text.RegularExpressions.Regex("\"role\":\"system\""));
        StringAssert.Contains(raw, "nudge");
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

    [TestMethod]
    public async Task Response_ReportsOneHourCacheWritesSeparately()
    {
        // 1-hour writes cost 2x input, 5-minute writes 1.25x; the adapter only reports the total.
        const string body = """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
             "content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":2,"cache_creation_input_tokens":700,"cache_read_input_tokens":0,
                      "cache_creation":{"ephemeral_5m_input_tokens":300,"ephemeral_1h_input_tokens":400}}}
            """;
        var (client, _) = Build(body: body);

        var response = await client.GetResponseAsync(Hello);

        Assert.AreEqual(700, response.Usage!.AdditionalCounts!["CacheCreationInputTokens"]);
        Assert.AreEqual(400, response.Usage.AdditionalCounts["CacheCreation1hInputTokens"]);
    }

    [TestMethod]
    public async Task Response_WithoutOneHourWrites_AddsNoBreakdown()
    {
        var (client, _) = Build();

        var response = await client.GetResponseAsync(Hello);

        Assert.IsFalse(response.Usage!.AdditionalCounts!.ContainsKey("CacheCreation1hInputTokens"));
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
