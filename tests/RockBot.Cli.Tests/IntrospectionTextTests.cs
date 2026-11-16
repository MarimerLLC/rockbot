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
    public void HealthSections_ReadAsPlainLines()
    {
        var response = Response() with
        {
            Mcp = new McpIntrospection
            {
                Servers =
                [
                    new McpServerIntrospection { Name = "calendar", Transport = "http", Host = "calendar.example.com", State = "connected", ToolCount = 12, Version = "1.10.0", UsesOAuth = true },
                    new McpServerIntrospection { Name = "todo", Transport = "stdio", State = "failed", LastError = "Connection refused" },
                ],
            },
            Skills = new SkillsIntrospection
            {
                Count = 2, Learned = 1, Starter = 1,
                Skills =
                [
                    new SkillIntrospection { Name = "getting-started", Source = "starter", Summary = "How to begin" },
                    new SkillIntrospection { Name = "travel-booking", Source = "learned", LastUsedAt = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero) },
                ],
            },
            Memory = new MemoryIntrospection
            {
                AuditedAt = new DateTimeOffset(2026, 11, 14, 4, 0, 0, TimeSpan.Zero), Status = "warning", Live = 120, Archived = 30,
                CreatedSinceLast = 22, NetGrowthPerDay = 2.5, NearDuplicatePairs = 3, PurgeDueCount = 4, PurgeDueWithinDays = 7,
                Findings = [new MemoryFindingIntrospection { Name = "chain-depth", Message = "2 chains too deep" }],
                ConsolidationPaused = true, ConsolidationPausedReason = "entries were hard-deleted",
            },
            Routing = new RoutingIntrospection
            {
                WindowDays = 7, Total = 40, UserMessages = 30, Subagents = 10, Fallbacks = 2, FallbackPct = 5.0,
                Tiers = [new TierRoutingIntrospection { Tier = "Balanced", Count = 30, Pct = 75.0, AvgLatencyMs = 4200, AvgInputTokens = 21_000, AvgOutputTokens = 900, AvgToolCalls = 2.5 }],
            },
        };

        var text = Render(response, IntrospectionSections.Mcp, IntrospectionSections.Skills, IntrospectionSections.Memory, IntrospectionSections.Routing);

        StringAssert.Contains(text, "MCP servers (1 of 2 connected)\n");
        StringAssert.Contains(text, "  calendar  connected  calendar.example.com (OAuth)  12 tools, v1.10.0\n");
        StringAssert.Contains(text, "  todo      failed     stdio  Connection refused\n");
        StringAssert.Contains(text, "Skills (2: 1 learned, 1 starter, 0 from MCP servers)\n");
        StringAssert.Contains(text, "  travel-booking  [learned, used 2026-11-14]\n");
        StringAssert.Contains(text, "  getting-started  [starter, never used]  How to begin\n");
        StringAssert.Contains(text, "Memory (warning; audited 2026-11-14 04:00:00Z)\n");
        StringAssert.Contains(text, "  ! chain-depth: 2 chains too deep\n");
        StringAssert.Contains(text, "  Consolidation is PAUSED: entries were hard-deleted\n");
        StringAssert.Contains(text, "Routing (last 7 days: 40 requests, 30 user messages, 10 subagent runs)\n");
        StringAssert.Contains(text, "  Balanced         30   75.0%      4.2s     21.0K       900     2.5\n");
        StringAssert.Contains(text, "2 request(s) (5.0%) were answered by a fallback model.");
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
