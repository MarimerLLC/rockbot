using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>The shared decision-model client against the response shapes Microsoft-Decision-1 returned.</summary>
[TestClass]
public class FoundryDecisionServiceTests
{
    /// <summary>A three-question response captured from Microsoft-Decision-1 on 2026-10-13.</summary>
    private const string ObservedMulti =
        """{"model":"microsoft-decision-1","answers":{"is_question":{"noul":0.9579123152329758,"type":"noul"},"severity":{"confidence":0.18265247118863592,"legend":{"0":"Not urgent","1":"Soon","2":"Today","3":"Immediately"},"probabilities":{"0":0.005520107315116434,"1":0.3415169057011938,"2":0.38698935339147694,"3":0.2659736335922129},"score":1.9134165132607863,"type":"score"},"tier":{"choice":"high","confidence":0.8546101227243663,"probabilities":{"balanced":0.09518324335256331,"high":0.9030734151495776,"low":0.0017433414978590054},"type":"choice"}},"usage":{"input_tokens":118,"output_tokens":3}}""";

    private static DecisionRequest MultiRequest() => new("test", "Fix the flaky test", new Dictionary<string, DecisionQuestion>
    {
        ["is_question"] = new NoulQuestion("Is the user asking a question?"),
        ["severity"] = new ScoreQuestion("How urgent?", ["Not urgent", "Soon", "Today", "Immediately"]),
        ["tier"] = new ChoiceQuestion("Which tier?", new Dictionary<string, string>
        {
            ["low"] = "simple", ["balanced"] = "ordinary", ["high"] = "hard",
        }),
    });

    [TestMethod]
    public void ParseResult_ReadsNoulChoiceAndScore()
    {
        var result = FoundryDecisionService.ParseResult(ObservedMulti)!;

        Assert.AreEqual("microsoft-decision-1", result.Model);
        Assert.AreEqual(0.958, result.Probability("is_question")!.Value, 0.001);
        Assert.AreEqual("high", result.Choice("tier"));
        Assert.AreEqual(0.903, result.Answers["tier"].Probabilities!["high"], 0.001);
        Assert.AreEqual(1.913, result.Answers["severity"].Score!.Value, 0.001);
        Assert.AreEqual(4, result.Answers["severity"].Probabilities!.Count);
    }

    [TestMethod]
    [DataRow("""{"answers":{"q":0.42}}""", 0.42)]
    [DataRow("""{"answers":{"q":{"probability":0.3}}}""", 0.3)]
    [DataRow("""{"answers":{"q":{"noul":1.7}}}""", 1.0)]
    public void ParseResult_AcceptsOtherNoulShapes(string body, double expected)
    {
        Assert.AreEqual(expected, FoundryDecisionService.ParseResult(body)!.Probability("q")!.Value, 1e-12);
    }

    [TestMethod]
    public void BuildBody_WritesEachQuestionType()
    {
        var body = FoundryDecisionService.BuildBody("decision-1", MultiRequest());

        Assert.AreEqual("decision-1", (string?)body["model"]);
        Assert.AreEqual("Fix the flaky test", (string?)body["state"]);
        Assert.AreEqual("noul", (string?)body["questions"]!["is_question"]!["type"]);
        Assert.IsNull(body["questions"]!["is_question"]!["criteria"], "no criteria unless given");
        Assert.AreEqual("score", (string?)body["questions"]!["severity"]!["type"]);
        Assert.AreEqual("Today", (string?)body["questions"]!["severity"]!["criteria"]![2]);
        Assert.AreEqual("hard", (string?)body["questions"]!["tier"]!["criteria"]!["high"]);
    }

    [TestMethod]
    public void BuildBody_SerializesObjectStateAndNoulCriteria()
    {
        var body = FoundryDecisionService.BuildBody("d", DecisionRequest.Single("p",
            new Dictionary<string, object?> { ["user_message"] = "hi", ["n"] = 3 }, "q",
            new NoulQuestion("?", WhenTrue: "yes means", WhenFalse: "no means")));

        Assert.AreEqual("hi", (string?)body["state"]!["user_message"]);
        Assert.AreEqual(3, (int?)body["state"]!["n"]);
        Assert.AreEqual("yes means", (string?)body["questions"]!["q"]!["criteria"]!["true"]);
    }

    [TestMethod]
    public async Task DecideAsync_PostsToSystemOneWithTheKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservedMulti);

        var result = await Service(handler).DecideAsync(MultiRequest());

        Assert.AreEqual("high", result!.Choice("tier"));
        Assert.AreEqual("https://acct.services.ai.azure.com/providers/microsoft/v1/systemone", handler.Uri!.ToString());
        Assert.AreEqual("test-key", handler.ApiKey);
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(handler.Body!), handler.ContentLength,
            "systemone needs Content-Length (HTTP 400 otherwise)");
        Assert.AreEqual("decision-1", (string?)JsonNode.Parse(handler.Body!)!["model"]);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task DecideAsync_HttpError_IsNull(HttpStatusCode status)
    {
        Assert.IsNull(await Service(new StubHandler(status, """{"error":{"code":"RateLimitReached"}}"""))
            .DecideAsync(MultiRequest()));
    }

    [TestMethod]
    public async Task DecideAsync_MissingAnswer_IsNull()
    {
        Assert.IsNull(await Service(new StubHandler(HttpStatusCode.OK, """{"answers":{"is_question":{"noul":0.5}}}"""))
            .DecideAsync(MultiRequest()), "every question must be answered");
    }

    [TestMethod]
    public async Task DecideAsync_Timeout_IsNull()
    {
        var service = Service(new StubHandler(HttpStatusCode.OK, ObservedMulti, delay: TimeSpan.FromSeconds(5)),
            o => o.TimeoutSeconds = 0.5);

        Assert.IsNull(await service.DecideAsync(MultiRequest()));
    }

    [TestMethod]
    public async Task PerPurposeSwitchAndThreshold()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservedMulti);
        var service = Service(handler, o =>
        {
            o.Uses["test"] = new DecisionUseOptions { Enabled = false };
            o.Uses[DecisionPurposes.Correction] = new DecisionUseOptions { Threshold = 0.8 };
        });

        Assert.IsFalse(service.IsEnabled("test"));
        Assert.IsTrue(service.IsEnabled(DecisionPurposes.Correction));
        Assert.IsNull(await service.DecideAsync(MultiRequest()));
        Assert.IsNull(handler.Uri, "a disabled purpose makes no call");
        Assert.AreEqual(0.8, service.ThresholdFor(DecisionPurposes.Correction, 0.5));
        Assert.AreEqual(0.5, service.ThresholdFor(DecisionPurposes.ActionGate, 0.5));
    }

    [TestMethod]
    public void Unconfigured_IsDisabledForEveryPurpose()
    {
        var service = new FoundryDecisionService(Options.Create(new DecisionModelOptions()));

        Assert.IsFalse(service.IsEnabled(DecisionPurposes.ActionGate));
    }

    [TestMethod]
    public void Options_BindFromTheHelmEnvKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DecisionModel:Endpoint"] = "https://acct.services.ai.azure.com",
                ["DecisionModel:ApiKey"] = "k",
                ["DecisionModel:Deployment"] = "decision-1",
                ["DecisionModel:Uses:Correction:Enabled"] = "false",
                ["DecisionModel:Uses:ActionGate:Threshold"] = "0.6",
            })
            .Build();
        var options = new DecisionModelOptions();

        config.GetSection("DecisionModel").Bind(options);

        Assert.IsTrue(options.IsConfigured);
        Assert.IsFalse(options.Uses[DecisionPurposes.Correction].Enabled);
        Assert.AreEqual(0.6, options.Uses[DecisionPurposes.ActionGate].Threshold);
    }

    private static FoundryDecisionService Service(StubHandler handler, Action<DecisionModelOptions>? configure = null)
    {
        var options = new DecisionModelOptions
        {
            Endpoint = "https://acct.services.ai.azure.com/",
            ApiKey = "test-key",
            Deployment = "decision-1",
        };
        configure?.Invoke(options);
        return new FoundryDecisionService(Options.Create(options), httpClient: new HttpClient(handler));
    }

    private sealed class StubHandler(HttpStatusCode status, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
            // Read before the body: Foundry rejects a request without Content-Length.
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (delay is { } d) await Task.Delay(d, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
