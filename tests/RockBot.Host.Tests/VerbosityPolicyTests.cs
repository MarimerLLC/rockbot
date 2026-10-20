using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class VerbosityPolicyTests
{
    private static string? Inject(string body, string verbosity = "high")
        => VerbosityPolicy.InjectInto(Encoding.UTF8.GetBytes(body), verbosity);

    [TestMethod]
    public void InjectInto_AddsFieldToChatCompletionBody()
    {
        var result = Inject("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.IsNotNull(result);
        var json = JsonNode.Parse(result)!.AsObject();
        Assert.AreEqual("high", (string?)json["verbosity"]);
        Assert.AreEqual("m", (string?)json["model"]);
    }

    [TestMethod]
    public void InjectInto_ReturnsNullForNonChatRequests()
    {
        Assert.IsNull(Inject("""{"model":"m","input":"some text"}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNullWhenCallerAlreadySetTheField()
    {
        Assert.IsNull(Inject(
            """{"messages":[{"role":"user","content":"hi"}],"verbosity":"low"}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNullForNonObjectOrMalformedBody()
    {
        Assert.IsNull(Inject("""[1,2,3]"""));
        Assert.IsNull(Inject("{not json"));
    }

    [TestMethod]
    [DataRow("high", "high")]
    [DataRow(" Medium ", "medium")]
    [DataRow("LOW", "low")]
    [DataRow("verbose", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Normalise_AcceptsOnlyKnownLevels(string? input, string? expected)
    {
        Assert.AreEqual(expected, VerbosityPolicy.Normalise(input));
    }
}
