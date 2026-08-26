using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class ReasoningEffortPolicyTests
{
    private static string? Inject(string body, string effort = "medium")
        => ReasoningEffortPolicy.InjectInto(Encoding.UTF8.GetBytes(body), effort);

    [TestMethod]
    public void InjectInto_AddsNestedReasoningObjectToChatCompletionBody()
    {
        var result = Inject("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.IsNotNull(result);
        var json = JsonNode.Parse(result)!.AsObject();
        // The provider expects an object, not a bare string — a flat "reasoning":"medium"
        // is rejected, so the shape matters as much as the value.
        Assert.AreEqual("medium", (string?)json["reasoning"]!["effort"]);
    }

    [TestMethod]
    public void InjectInto_PreservesExistingFields()
    {
        var result = Inject("""
            {"model":"m","temperature":0.95,"frequency_penalty":0.5,
             "messages":[{"role":"user","content":"hi"}]}
            """);

        var json = JsonNode.Parse(result!)!.AsObject();
        Assert.AreEqual("m", (string?)json["model"]);
        Assert.AreEqual(0.95f, (float)json["temperature"]!);
        Assert.AreEqual(0.5f, (float)json["frequency_penalty"]!);
        Assert.AreEqual(1, json["messages"]!.AsArray().Count);
    }

    [TestMethod]
    public void InjectInto_ReturnsNullForNonChatRequests()
    {
        // Embeddings and similar calls reject an unknown field.
        Assert.IsNull(Inject("""{"model":"m","input":"some text"}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNullWhenCallerAlreadySetTheField()
    {
        Assert.IsNull(Inject(
            """{"messages":[{"role":"user","content":"hi"}],"reasoning":{"effort":"high"}}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNullForNonObjectBody()
    {
        Assert.IsNull(Inject("""[1,2,3]"""));
    }

    [TestMethod]
    public void InjectInto_ThrowsOnMalformedJson_SoCallerCanFailOpen()
    {
        // The policy wraps this in try/catch and sends the original body unchanged;
        // the contract here is simply that it does not silently corrupt the request.
        Assert.Throws<System.Text.Json.JsonException>(() => Inject("{not json"));
    }

    [TestMethod]
    [DataRow("low", "low")]
    [DataRow("medium", "medium")]
    [DataRow("high", "high")]
    [DataRow("  HIGH  ", "high")]
    [DataRow("Medium", "medium")]
    public void Normalize_AcceptsTheValuesTheProviderUnderstands(string input, string expected)
        => Assert.AreEqual(expected, ReasoningEffortPolicy.Normalize(input));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("none")]
    [DataRow("maximum")]
    [DataRow("1")]
    public void Normalize_RejectsAnythingElse(string? input)
    {
        // Rejected rather than forwarded: the provider drops an unrecognised effort silently,
        // so a typo would be indistinguishable from a working setting.
        Assert.IsNull(ReasoningEffortPolicy.Normalize(input));
    }
}
