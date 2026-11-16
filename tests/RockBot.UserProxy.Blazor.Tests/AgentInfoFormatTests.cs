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
    [DataRow(0.0, "$0.00")]
    [DataRow(0.004, "<$0.01")]
    [DataRow(1.5, "$1.50")]
    [DataRow(1234.567, "$1,234.57")]
    public void Cost(double usd, string expected) => Assert.AreEqual(expected, AgentInfoFormat.Cost(usd));

    [TestMethod]
    [DataRow(950L, "950")]
    [DataRow(9_999L, "9,999")]
    [DataRow(12_345L, "12.3K")]
    [DataRow(4_560_000L, "4.56M")]
    public void Tokens(long count, string expected) => Assert.AreEqual(expected, AgentInfoFormat.Tokens(count));

    [TestMethod]
    public void CacheShare_AndDay()
    {
        Assert.AreEqual("62% cached", AgentInfoFormat.CacheShare(new UsageTotalsIntrospection { InputTokens = 1000, CachedInputTokens = 620 }));
        Assert.IsNull(AgentInfoFormat.CacheShare(new UsageTotalsIntrospection()));
        Assert.AreEqual("Sun, Nov 15", AgentInfoFormat.Day("2026-11-15"));
        Assert.AreEqual("garbage", AgentInfoFormat.Day("garbage"));
    }

    [TestMethod]
    [DataRow("connected", "ok")]
    [DataRow("healthy", "ok")]
    [DataRow("failed", "bad")]
    [DataRow("sign-in required", "bad")]
    [DataRow("alert", "bad")]
    [DataRow("sign-in pending", "warn")]
    [DataRow("warning", "warn")]
    [DataRow("not connected", "idle")]
    public void StateTone(string state, string tone) => Assert.AreEqual(tone, AgentInfoFormat.StateTone(state));

    [TestMethod]
    public void McpWording()
    {
        Assert.AreEqual("Needs sign-in", AgentInfoFormat.McpState("sign-in required"));
        Assert.AreEqual("Failed to connect", AgentInfoFormat.McpState("failed"));
        Assert.AreEqual("1 tool", AgentInfoFormat.McpCounts(new McpServerIntrospection { ToolCount = 1 }));
        Assert.AreEqual("12 tools · 2 prompts · 1 resource",
            AgentInfoFormat.McpCounts(new McpServerIntrospection { ToolCount = 12, PromptCount = 2, ResourceCount = 1 }));
    }

    [TestMethod]
    public void HealthNumbers()
    {
        Assert.AreEqual("73%", AgentInfoFormat.Percent(0.734));
        Assert.AreEqual("+2.5", AgentInfoFormat.Growth(2.5));
        Assert.AreEqual("-1.0", AgentInfoFormat.Growth(-1));
        Assert.AreEqual("850ms", AgentInfoFormat.Seconds(850));
        Assert.AreEqual("4.2s", AgentInfoFormat.Seconds(4200));
        Assert.AreEqual("just now", AgentInfoFormat.Ago(TimeSpan.FromSeconds(20)));
        Assert.AreEqual("3h ago", AgentInfoFormat.Ago(TimeSpan.FromHours(3.5)));
        Assert.AreEqual("2d ago", AgentInfoFormat.Ago(TimeSpan.FromDays(2.2)));
        Assert.AreEqual("6w ago", AgentInfoFormat.Ago(TimeSpan.FromDays(43)));
    }

    [TestMethod]
    [DataRow(30, "under a minute")]
    [DataRow(42 * 60, "42m")]
    [DataRow(5 * 3600 + 12 * 60, "5h 12m")]
    [DataRow(3 * 86400 + 4 * 3600 + 59 * 60, "3d 4h")]
    public void Uptime(int seconds, string expected) =>
        Assert.AreEqual(expected, AgentInfoFormat.Uptime(TimeSpan.FromSeconds(seconds)));
}
