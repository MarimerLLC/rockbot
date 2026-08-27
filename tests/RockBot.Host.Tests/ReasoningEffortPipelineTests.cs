using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Exercises the policy through a real <see cref="ClientPipeline"/> rather than calling the
/// body-rewrite helper directly. The helper being correct does not prove the policy actually
/// mutates the request that goes on the wire.
/// </summary>
[TestClass]
public sealed class ReasoningEffortPipelineTests
{
    /// <summary>Captures the request body at the HTTP boundary — the real wire format.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task<string> SendThroughAsync(string effort, string body)
    {
        var handler = new CapturingHandler();
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        var options = new ClientPipelineOptions { Transport = transport };
        options.AddPolicy(new ReasoningEffortPolicy(effort), PipelinePosition.PerCall);

        var pipeline = ClientPipeline.Create(options);
        var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.test/v1/chat/completions");
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(body));

        await pipeline.SendAsync(message);
        return handler.CapturedBody!;
    }

    [TestMethod]
    public async Task Policy_RewritesTheBodyThatReachesTheTransport()
    {
        var sent = await SendThroughAsync("low",
            """{"model":"x-ai/grok-4.6","temperature":0.95,"messages":[{"role":"user","content":"hi"}]}""");

        var json = JsonNode.Parse(sent)!.AsObject();
        Assert.IsTrue(json.ContainsKey("reasoning"),
            $"reasoning missing from body actually sent: {sent}");
        Assert.AreEqual("low", json["reasoning"]!["effort"]!.GetValue<string>());
        Assert.AreEqual(0.95f, (float)json["temperature"]!);
    }

    [TestMethod]
    public async Task Policy_LeavesNonChatBodiesAlone()
    {
        var sent = await SendThroughAsync("low", """{"model":"m","input":"text"}""");

        Assert.IsFalse(JsonNode.Parse(sent)!.AsObject().ContainsKey("reasoning"));
    }

    [TestMethod]
    public async Task Policy_CoexistsWithRepetitionPenalty()
    {
        // Both policies rewrite the same body; the second must not drop the first's field.
        var handler = new CapturingHandler();
        var options = new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        options.AddPolicy(new RepetitionPenaltyPolicy(1.1f), PipelinePosition.PerCall);
        options.AddPolicy(new ReasoningEffortPolicy("low"), PipelinePosition.PerCall);

        var pipeline = ClientPipeline.Create(options);
        var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.test/v1/chat/completions");
        message.Request.Content = BinaryContent.Create(BinaryData.FromString(
            """{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));

        await pipeline.SendAsync(message);

        var json = JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        Assert.AreEqual(1.1f, (float)json["repetition_penalty"]!);
        Assert.AreEqual("low", json["reasoning"]!["effort"]!.GetValue<string>());
    }
}
