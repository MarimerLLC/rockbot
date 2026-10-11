using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using RockBot.Host;

namespace RockBot.Agent.Tests;

/// <summary>
/// #693: the key reaches the wire through the same stack the agent uses — the OpenAI SDK client
/// behind the Microsoft.Extensions.AI adapter — not just through a bare pipeline.
/// </summary>
[TestClass]
public sealed class PromptCacheKeyWireTests
{
    private const string Completion = """
        {"id":"c1","object":"chat.completion","created":1,"model":"gpt-test",
         "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":5,"completion_tokens":1,"total_tokens":6}}
        """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Completion, Encoding.UTF8, "application/json")
            };
        }
    }

    [TestMethod]
    public async Task ChatClient_SendsTheKey_InsideARun()
    {
        var handler = new CapturingHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.cognitiveservices.azure.com/openai/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };
        options.AddPolicy(new PromptCacheKeyPolicy(_ => { }), PipelinePosition.PerCall);
        var chat = new OpenAIClient(new ApiKeyCredential("test"), options).GetChatClient("gpt-test").AsIChatClient();

        using (ToolCallSessionContext.Set("cli-session"))
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var keyed = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.AreEqual(PromptCacheKeyPolicy.Key("cli-session", "gpt-test"), keyed["prompt_cache_key"]?.GetValue<string>(),
            $"body sent: {handler.Bodies[0]}");
        Assert.IsFalse(JsonNode.Parse(handler.Bodies[1])!.AsObject().ContainsKey("prompt_cache_key"));
    }
}
