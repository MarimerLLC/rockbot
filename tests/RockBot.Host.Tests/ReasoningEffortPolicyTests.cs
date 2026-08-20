using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the body-rewrite helper. The wire-level behaviour is covered separately by
/// <see cref="ReasoningEffortPipelineTests"/>.
/// </summary>
[TestClass]
public sealed class ReasoningEffortPolicyTests
{
    private static string? Inject(string body, string effort) =>
        ReasoningEffortPolicy.InjectInto(Encoding.UTF8.GetBytes(body), effort);

    private const string ChatBody = """{"model":"x-ai/grok-4.6","messages":[{"role":"user","content":"hi"}]}""";

    [TestMethod]
    public void InjectInto_AddsNestedReasoningObject_NotFlatReasoningEffort()
    {
        // The whole reason this policy exists: OpenRouter accepts and ignores the flat
        // reasoning_effort field, so emitting it instead would be a silent no-op.
        var json = (JsonObject)JsonNode.Parse(Inject(ChatBody, "low")!)!;

        Assert.IsFalse(json.ContainsKey("reasoning_effort"),
            "flat reasoning_effort is ignored by OpenRouter and must not be sent");
        Assert.AreEqual("low", json["reasoning"]!["effort"]!.GetValue<string>());
    }

    [TestMethod]
    public void InjectInto_None_DisablesReasoningRatherThanNamingALevel()
    {
        var json = (JsonObject)JsonNode.Parse(Inject(ChatBody, "none")!)!;

        Assert.IsFalse(json["reasoning"]!.AsObject().ContainsKey("effort"));
        Assert.IsFalse(json["reasoning"]!["enabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public void InjectInto_PreservesExistingBodyFields()
    {
        var json = (JsonObject)JsonNode.Parse(Inject(ChatBody, "high")!)!;

        Assert.AreEqual("x-ai/grok-4.6", json["model"]!.GetValue<string>());
        Assert.AreEqual(1, json["messages"]!.AsArray().Count);
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_ForNonChatCompletionBody()
    {
        // Embeddings and other calls share the pipeline and reject the field.
        Assert.IsNull(Inject("""{"model":"nomic-embed-text","input":"hi"}""", "low"));
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_WhenCallerAlreadySetReasoning()
    {
        Assert.IsNull(Inject(
            """{"messages":[],"reasoning":{"effort":"high"}}""", "low"));
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_ForUnparseableOrNonObjectBody()
    {
        Assert.IsNull(Inject("not json at all", "low"));
        Assert.IsNull(Inject("[1,2,3]", "low"));
    }

    [DataTestMethod]
    [DataRow("low", "low")]
    [DataRow("  HIGH  ", "high")]
    [DataRow("Medium", "medium")]
    [DataRow("minimal", "minimal")]
    [DataRow("none", "none")]
    [DataRow("off", "none")]
    [DataRow("Disabled", "none")]
    public void Normalise_AcceptsKnownLevelsCaseAndWhitespaceInsensitively(string input, string expected)
        => Assert.AreEqual(expected, ReasoningEffortPolicy.Normalise(input));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("lowest")]
    [DataRow("verbose")]
    [DataRow("2")]
    public void Normalise_RejectsUnknownValues_SoTyposDoNot400EveryCall(string? input)
        => Assert.IsNull(ReasoningEffortPolicy.Normalise(input));
}
