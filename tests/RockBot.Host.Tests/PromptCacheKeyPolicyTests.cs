using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RockBot.Host.Tests;

/// <summary>
/// #693: <see cref="PromptCacheKeyPolicy"/> adds a per-session <c>prompt_cache_key</c> to chat
/// completions sent to OpenAI / Azure OpenAI, and stops sending it if an endpoint rejects it.
/// </summary>
[TestClass]
public sealed class PromptCacheKeyPolicyTests
{
    private const string ChatBody = """{"model":"gpt-5.6-terra","messages":[{"role":"user","content":"hi"}]}""";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [TestMethod]
    public void InjectInto_AddsTheSessionModelKey()
    {
        var json = JsonNode.Parse(PromptCacheKeyPolicy.InjectInto(Utf8(ChatBody), "cli-session")!)!.AsObject();

        Assert.AreEqual(PromptCacheKeyPolicy.Key("cli-session", "gpt-5.6-terra"), json["prompt_cache_key"]!.GetValue<string>());
        Assert.AreEqual("gpt-5.6-terra", json["model"]!.GetValue<string>(), "the rest of the body is kept");
    }

    [TestMethod]
    public void InjectInto_LeavesOtherBodiesAlone()
    {
        Assert.IsNull(PromptCacheKeyPolicy.InjectInto(Utf8("""{"model":"m","input":"embed me"}"""), "s"), "not a chat completion");
        Assert.IsNull(PromptCacheKeyPolicy.InjectInto(Utf8("""{"messages":[],"prompt_cache_key":"caller"}"""), "s"), "caller's key wins");
        Assert.IsNull(PromptCacheKeyPolicy.InjectInto(Utf8("not json"), "s"));
        Assert.IsNull(PromptCacheKeyPolicy.InjectInto(Utf8("[1,2]"), "s"));
    }

    [TestMethod]
    public void Key_IsStablePerSessionAndModel_AndHidesTheSessionId()
    {
        var key = PromptCacheKeyPolicy.Key("blazor-session", "gpt-5.6-terra");

        Assert.AreEqual(key, PromptCacheKeyPolicy.Key("blazor-session", "gpt-5.6-terra"));
        Assert.AreNotEqual(key, PromptCacheKeyPolicy.Key("subagent-abc", "gpt-5.6-terra"));
        Assert.AreNotEqual(key, PromptCacheKeyPolicy.Key("blazor-session", "gpt-5.6-luna"));
        Assert.IsTrue(key.StartsWith("rb-", StringComparison.Ordinal) && key.Length == 35);
        Assert.IsFalse(key.Contains("blazor", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("https://api.openai.com/v1", true)]
    [DataRow("https://my-res.openai.azure.com/openai/v1/", true)]
    [DataRow("https://my-res-eastus2.cognitiveservices.azure.com/openai/v1/", true)]
    [DataRow("https://my-proj.services.ai.azure.com/openai/v1/", true)]
    [DataRow("https://openrouter.ai/api/v1", false)]
    [DataRow("http://ollama.default.svc.cluster.local:11434/v1", false)]
    public void AppliesTo_OnlyOpenAiAndAzure(string endpoint, bool expected) =>
        Assert.AreEqual(expected, PromptCacheKeyPolicy.AppliesTo(new Uri(endpoint)));

    // ── Through a real pipeline ──────────────────────────────────────────────

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task SendAsync(ClientPipeline pipeline, string body)
    {
        var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.openai.azure.com/openai/v1/chat/completions");
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(body));
        await pipeline.SendAsync(message);
    }

    private static ClientPipeline Pipeline(ScriptedHandler handler, PromptCacheKeyPolicy policy)
    {
        var options = new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        options.AddPolicy(policy, PipelinePosition.PerCall);
        return ClientPipeline.Create(options);
    }

    private static bool HasKey(string body) => JsonNode.Parse(body)!.AsObject().ContainsKey("prompt_cache_key");

    [TestMethod]
    public async Task Pipeline_SendsTheKey_OnlyInsideARun()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{}"));
        var pipeline = Pipeline(handler, new PromptCacheKeyPolicy(_ => { }));

        await SendAsync(pipeline, ChatBody);
        using (ToolCallSessionContext.Set("cli-session"))
            await SendAsync(pipeline, ChatBody);

        Assert.IsFalse(HasKey(handler.Sent[0]), "no session, no key");
        Assert.IsTrue(HasKey(handler.Sent[1]), $"key missing from the body actually sent: {handler.Sent[1]}");
    }

    [TestMethod]
    public async Task Pipeline_RejectedKey_IsResentWithoutIt_AndNotSentAgain()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest, """{"error":{"message":"Unrecognized request argument supplied: prompt_cache_key"}}"""),
            (HttpStatusCode.OK, "{}"));
        var warnings = new List<string>();
        var policy = new PromptCacheKeyPolicy(warnings.Add);
        var pipeline = Pipeline(handler, policy);

        using (ToolCallSessionContext.Set("cli-session"))
        {
            await SendAsync(pipeline, ChatBody);
            await SendAsync(pipeline, ChatBody);
        }

        Assert.AreEqual(3, handler.Sent.Count, "the rejected call is re-sent once, then one normal call");
        Assert.IsTrue(HasKey(handler.Sent[0]));
        Assert.IsFalse(HasKey(handler.Sent[1]), "the re-send drops the key");
        Assert.IsFalse(HasKey(handler.Sent[2]), "later calls no longer carry it");
        Assert.IsTrue(policy.Disabled);
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public async Task Pipeline_OtherBadRequests_AreNotRetried()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, """{"error":{"message":"context_length_exceeded"}}"""));
        var policy = new PromptCacheKeyPolicy(_ => { });
        var pipeline = Pipeline(handler, policy);

        using (ToolCallSessionContext.Set("cli-session"))
            await SendAsync(pipeline, ChatBody);

        Assert.AreEqual(1, handler.Sent.Count);
        Assert.IsFalse(policy.Disabled);
    }
}
