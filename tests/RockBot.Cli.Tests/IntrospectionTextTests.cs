using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

/// <summary><c>rockbot info --models/--pricing</c> output (#834).</summary>
[TestClass]
public sealed class IntrospectionTextTests
{
    private static readonly PriceIntrospection Haiku = new() { Prefix = "claude-haiku-5-5", InputPerM = 0.10, OutputPerM = 0.50, CachedInputPerM = 0.01 };

    private static AgentIntrospectionResponse Response() => new()
    {
        AgentName = "Rocky",
        AgentVersion = "0.18.4",
        StartedAt = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero),
        Models = new ModelsIntrospection
        {
            LoadedAt = new DateTimeOffset(2026, 11, 15, 8, 0, 1, TimeSpan.Zero),
            Tiers =
            [
                new TierIntrospection
                {
                    Tier = "Low",
                    Models = [new ModelIntrospection { Service = "Azure AI Foundry", EndpointHost = "x.cognitiveservices.azure.com", ModelId = "gpt-5.6-luna" }],
                },
                new TierIntrospection
                {
                    Tier = "Balanced",
                    AcceptsImages = true,
                    Models =
                    [
                        new ModelIntrospection { Service = "Anthropic API", EndpointHost = "api.anthropic.com", ModelId = "claude-haiku-5-5", SupportsImageInput = true, Price = Haiku },
                        new ModelIntrospection { Service = "OpenRouter", EndpointHost = "openrouter.ai", ModelId = "house-model" },
                    ],
                },
                new TierIntrospection { Tier = "High", UsesTier = "Balanced" },
            ],
            DecisionModel = new DecisionModelIntrospection { Summary = "Decision model: decision-1 (Azure AI Foundry, Foundry decision API)" },
        },
        Pricing = new PricingIntrospection { Rows = [Haiku, new PriceIntrospection { Prefix = "gpt-5.6-luna", InputPerM = 0.2, OutputPerM = 1.2 }] },
    };

    private static string Render(AgentIntrospectionResponse response, params string[] sections)
    {
        using var output = new StringWriter { NewLine = "\n" };
        IntrospectionText.Write(output, response, sections);
        return output.ToString();
    }

    [TestMethod]
    public void Models_ListEachTierWithItsChainAndPrices()
    {
        var text = Render(Response(), IntrospectionSections.Models);

        StringAssert.StartsWith(text, "Rocky 0.18.4 (started 2026-11-15 08:00:00Z)\n");
        StringAssert.Contains(text, "Models (loaded 2026-11-15 08:00:01Z");
        StringAssert.Contains(text, "  Low       gpt-5.6-luna (Azure AI Foundry, x.cognitiveservices.azure.com)  (no pricing row; costed at $0)\n");
        StringAssert.Contains(text, "  Balanced  1. claude-haiku-5-5 (Anthropic API, api.anthropic.com, images)  $0.10 in / $0.50 out per M\n");
        StringAssert.Contains(text, "            2. house-model (OpenRouter, openrouter.ai, text only)");
        StringAssert.Contains(text, "  High      (no model of its own; uses the Balanced model)\n");
        StringAssert.Contains(text, "fallback chain");
        StringAssert.Contains(text, "  Embeddings: none");
        StringAssert.Contains(text, "  Decision model: decision-1");
        Assert.IsFalse(text.Contains("Pricing", StringComparison.Ordinal), "only the sections asked for");
    }

    [TestMethod]
    public void Pricing_IsATableAndSaysWhereItCameFrom()
    {
        var text = Render(Response(), IntrospectionSections.Pricing);

        StringAssert.Contains(text, "built-in fallback table");
        StringAssert.Contains(text, "  prefix               input    output    cached\n");
        StringAssert.Contains(text, "  claude-haiku-5-5      0.10      0.50     0.010\n");
        StringAssert.Contains(text, "  gpt-5.6-luna          0.20      1.20         -\n");

        var fromFile = Render(Response() with { Pricing = Response().Pricing! with { LoadedFrom = "/data/agent/llm-pricing.json" } },
            IntrospectionSections.Pricing);
        StringAssert.Contains(fromFile, "Pricing (from /data/agent/llm-pricing.json)");
    }

    [TestMethod]
    public void Usage_IsATableOfRangesModelsAndWorkloads()
    {
        var totals = new UsageTotalsIntrospection { Requests = 1234, InputTokens = 4_560_000, CachedInputTokens = 2_000_000, OutputTokens = 12_300, CostUsd = 1.5 };
        var response = Response() with
        {
            Usage = new UsageIntrospection
            {
                TimeZone = "America/Chicago",
                Today = "2026-11-15",
                TodayTotals = totals,
                Last7Days = totals,
                Last30Days = totals with { UnpricedRequests = 3 },
                Models = [new ModelUsageIntrospection { ModelId = "claude-haiku-5-5", Totals = totals }],
                Kinds = [new KindUsageIntrospection { Kind = "patrol", Totals = totals }],
                Thread = new ThreadUsageIntrospection { ThreadId = "cli-session", Totals = totals },
            },
        };

        var text = Render(response, IntrospectionSections.Usage);

        StringAssert.Contains(text, "Usage (estimated from the pricing table; days in America/Chicago)");
        StringAssert.Contains(text, "  Today (2026-11-15)             1,234       4.56M       2.00M       12.3K       $1.50\n");
        StringAssert.Contains(text, "  This session (cli-session)");
        StringAssert.Contains(text, "  By model, last 30 days");
        StringAssert.Contains(text, "    claude-haiku-5-5");
        StringAssert.Contains(text, "    Scheduled tasks");
        StringAssert.Contains(text, "3 request(s) in the last 30 days matched no pricing row");
    }

    [TestMethod]
    [DataRow(0.0, "$0.00")]
    [DataRow(0.0027, "<$0.01")]
    [DataRow(1234.5, "$1,234.50")]
    public void Cost_KeepsASliverVisible(double usd, string expected) =>
        Assert.AreEqual(expected, IntrospectionText.Cost(usd));

    [TestMethod]
    public void ASectionTheAgentLeftOut_IsSaidSo()
    {
        var text = Render(Response() with { Models = null, Pricing = null });

        StringAssert.Contains(text, "Models: (the agent did not report its models)");
        StringAssert.Contains(text, "Pricing: (the agent did not report its pricing table)");
    }
}
