using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>How the agent info dialog (#834) words what the agent reports.</summary>
[TestClass]
public sealed class AgentInfoFormatTests
{
    [TestMethod]
    [DataRow(4.0, "$4.00")]
    [DataRow(0.5, "$0.50")]
    [DataRow(0.1, "$0.10")]
    [DataRow(0.01, "$0.010")]
    [DataRow(0.075, "$0.075")]
    [DataRow(0.0, "$0.00")]
    public void Money_KeepsSmallRatesReadable(double rate, string expected) =>
        Assert.AreEqual(expected, AgentInfoFormat.Money(rate));

    [TestMethod]
    public void Price_SaysWhenAModelIsCostedAtZero()
    {
        Assert.AreEqual("No pricing row: costed at $0", AgentInfoFormat.Price(null));
        Assert.AreEqual("$0.10 in · $0.50 out · $0.010 cached per M tokens",
            AgentInfoFormat.Price(new PriceIntrospection { InputPerM = 0.1, OutputPerM = 0.5, CachedInputPerM = 0.01 }));
        Assert.AreEqual("$2.00 in · $12.00 out per M tokens",
            AgentInfoFormat.Price(new PriceIntrospection { InputPerM = 2, OutputPerM = 12 }));
    }

    [TestMethod]
    public void CacheRate_ShowsTheDefaultWhenTheRowLeavesItOut()
    {
        Assert.AreEqual("1.25×", AgentInfoFormat.CacheRate(null, "1.25×"));
        Assert.AreEqual("$0.20", AgentInfoFormat.CacheRate(0.2, "1.25×"));
    }

    [TestMethod]
    public void Tags_NameTheServiceHostEffortAndImages()
    {
        var model = new ModelIntrospection
        {
            Service = "Azure AI Foundry", EndpointHost = "rocky.cognitiveservices.azure.com",
            ReasoningEffort = "low", SupportsImageInput = false,
        };

        CollectionAssert.AreEqual(
            new[] { "Azure AI Foundry", "rocky.cognitiveservices.azure.com", "reasoning: low", "text only" },
            AgentInfoFormat.Tags(model, new TierIntrospection { AcceptsImages = true }).ToArray());

        // A tier that never gets images says nothing about them; a host that is the service isn't repeated.
        CollectionAssert.AreEqual(
            new[] { "GitHub Copilot" },
            AgentInfoFormat.Tags(new ModelIntrospection { Service = "GitHub Copilot", EndpointHost = "github copilot" },
                new TierIntrospection()).ToArray());
    }

    [TestMethod]
    [DataRow(30, "under a minute")]
    [DataRow(42 * 60, "42m")]
    [DataRow(5 * 3600 + 12 * 60, "5h 12m")]
    [DataRow(3 * 86400 + 4 * 3600 + 59 * 60, "3d 4h")]
    public void Uptime(int seconds, string expected) =>
        Assert.AreEqual(expected, AgentInfoFormat.Uptime(TimeSpan.FromSeconds(seconds)));
}
