using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Exercises the attribution policy through a real <see cref="ClientPipeline"/>. The headers
/// only matter if they survive to the HTTP boundary, so they are asserted on the
/// <see cref="HttpRequestMessage"/> the transport actually sends rather than on the
/// <see cref="PipelineRequest"/> the policy touched.
/// </summary>
[TestClass]
public sealed class OpenRouterAttributionPolicyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task<HttpRequestMessage> SendThroughAsync(
        params OpenRouterAttributionPolicy[] policies)
    {
        var handler = new CapturingHandler();
        var options = new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        foreach (var policy in policies)
            options.AddPolicy(policy, PipelinePosition.PerCall);

        var pipeline = ClientPipeline.Create(options);
        var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://openrouter.ai/api/v1/chat/completions");
        message.Request.Content = BinaryContent.Create(
            BinaryData.FromString("""{"model":"m","messages":[]}"""));

        await pipeline.SendAsync(message);
        return handler.Captured!;
    }

    private static string? HeaderValue(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;

    [TestMethod]
    public async Task Policy_StampsBothAttributionHeadersOnTheWire()
    {
        var sent = await SendThroughAsync(
            new OpenRouterAttributionPolicy("muse", "https://example.test/muse"));

        Assert.AreEqual("https://example.test/muse", HeaderValue(sent, "HTTP-Referer"));
        Assert.AreEqual("muse", HeaderValue(sent, "X-Title"));
    }

    [TestMethod]
    public async Task Policy_DoesNotSendTheStandardRefererSpelling()
    {
        // OpenRouter reads HTTP-Referer specifically; sending Referer instead attributes
        // nothing, so a future "fix" that normalises the name would silently regress.
        var sent = await SendThroughAsync(
            new OpenRouterAttributionPolicy("rockbot", "https://example.test"));

        Assert.IsNull(sent.Headers.Referrer);
    }

    [TestMethod]
    public async Task Policy_ReplacesRatherThanAppendsWhenAppliedTwice()
    {
        // A retry re-runs the per-call pipeline over the same message. Add() would leave
        // "a,b" in the header and OpenRouter would attribute to neither app.
        var sent = await SendThroughAsync(
            new OpenRouterAttributionPolicy("first", "https://first.test"),
            new OpenRouterAttributionPolicy("second", "https://second.test"));

        Assert.AreEqual("second", HeaderValue(sent, "X-Title"));
        Assert.AreEqual("https://second.test", HeaderValue(sent, "HTTP-Referer"));
    }

    [TestMethod]
    [DataRow("https://openrouter.ai/api/v1", true)]
    [DataRow("https://OpenRouter.AI/api/v1", true)]
    [DataRow("https://sub.openrouter.ai/api/v1", true)]
    [DataRow("http://ollama.default.svc.cluster.local:11434/v1", false)]
    [DataRow("https://rocky-ml1nznjr-eastus2.openai.azure.com/openai/v1", false)]
    // Host match, not substring: a proxy that merely mentions openrouter in its path is a
    // different server and must not be handed the app identity.
    [DataRow("https://gateway.internal/openrouter/v1", false)]
    [DataRow("https://openrouter.ai.evil.test/v1", false)]
    [DataRow("not a url", false)]
    [DataRow(null, false)]
    public void IsOpenRouterEndpoint_MatchesOnHostOnly(string? endpoint, bool expected)
        => Assert.AreEqual(expected, OpenRouterAttributionPolicy.IsOpenRouterEndpoint(endpoint));

    [TestMethod]
    public void Defaults_AreTheDocumentedFallbacks()
    {
        Assert.AreEqual("rockbot", OpenRouterAttributionPolicy.DefaultAppName);
        Assert.AreEqual("https://rockbot.dev", OpenRouterAttributionPolicy.DefaultAppUrl);
    }
}
