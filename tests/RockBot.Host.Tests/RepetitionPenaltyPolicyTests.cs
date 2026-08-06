using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class RepetitionPenaltyPolicyTests
{
    private static string? Inject(string body, float penalty = 1.1f)
        => RepetitionPenaltyPolicy.InjectInto(Encoding.UTF8.GetBytes(body), penalty);

    [TestMethod]
    public void InjectInto_AddsFieldToChatCompletionBody()
    {
        var result = Inject("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.IsNotNull(result);
        var json = JsonNode.Parse(result)!.AsObject();
        Assert.AreEqual(1.1f, (float)json["repetition_penalty"]!);
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
        // Embeddings and similar calls reject an unknown sampling field.
        Assert.IsNull(Inject("""{"model":"m","input":"some text"}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNullWhenCallerAlreadySetTheField()
    {
        Assert.IsNull(Inject(
            """{"messages":[{"role":"user","content":"hi"}],"repetition_penalty":1.5}"""));
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
}
