using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Agent.McpBridge;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="AgentIntrospectionRequest"/> (#834): describes the agent's configuration
/// (its models, its pricing table), what it has used, and its health (MCP servers, skills,
/// memory, tier routing) from the process's own state, so a user interface can show it
/// without an LLM round trip. Deterministic, no LLM invocation; read-only.
/// </summary>
internal sealed class AgentIntrospectionRequestHandler(
    IMessagePublisher publisher,
    AgentNameHolder agentNameHolder,
    ILogger<AgentIntrospectionRequestHandler> logger,
    LlmConfigSnapshot? llmConfig = null,
    LlmCostEstimator? costEstimator = null,
    LlmUsageLedger? usageLedger = null,
    McpBridgeService? mcpBridge = null,
    ISkillStore? skillStore = null,
    TierRoutingLogger? routingLog = null,
    IOptions<MemoryAuditOptions>? auditOptions = null,
    IOptions<AgentProfileOptions>? profileOptions = null) : IMessageHandler<AgentIntrospectionRequest>
{
    private static readonly DateTimeOffset ProcessStartedAt = GetProcessStart();

    public async Task HandleAsync(AgentIntrospectionRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("AgentIntrospectionRequest received with no replyTo — ignoring");
            return;
        }

        var response = Build(
            message,
            agentNameHolder.DisplayName ?? context.Agent.Name,
            AssemblyVersion.Current,
            ProcessStartedAt,
            llmConfig,
            costEstimator,
            usageLedger);
        response = await AddHealthAsync(response, message);

        var envelope = response.ToEnvelope<AgentIntrospectionResponse>(
            source: context.Agent.Name,
            correlationId: context.Envelope.CorrelationId);

        await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

        logger.LogDebug("Published agent introspection: models={Models} pricing={Pricing} usage={Usage} mcp={Mcp} skills={Skills} memory={Memory} routing={Routing}",
            response.Models is not null, response.Pricing is not null, response.Usage is not null,
            response.Mcp is not null, response.Skills is not null, response.Memory is not null, response.Routing is not null);
    }

    /// <summary>
    /// Adds the agent-health sections (stage 4) that were asked for and whose source this agent
    /// has. Each is built on its own: one that fails is left out and logged, the rest still go.
    /// </summary>
    private async Task<AgentIntrospectionResponse> AddHealthAsync(AgentIntrospectionResponse response, AgentIntrospectionRequest request)
    {
        var wanted = Wanted(request);

        if (wanted.Contains(IntrospectionSections.Mcp) && mcpBridge is not null)
            response = response with { Mcp = await TryAsync("mcp", () => Task.FromResult(AgentHealthSections.Mcp(mcpBridge))) };
        if (wanted.Contains(IntrospectionSections.Skills) && skillStore is not null)
            response = response with { Skills = await TryAsync("skills", () => AgentHealthSections.SkillsAsync(skillStore)) };
        if (wanted.Contains(IntrospectionSections.Memory) && auditOptions is not null && profileOptions is not null)
            response = response with
            {
                Memory = await TryAsync("memory", () => AgentHealthSections.MemoryAsync(
                    AgentHealthSections.AuditRoot(auditOptions.Value, profileOptions.Value), logger)),
            };
        if (wanted.Contains(IntrospectionSections.Routing) && routingLog is not null)
            response = response with { Routing = await TryAsync("routing", () => AgentHealthSections.RoutingAsync(routingLog, DateTimeOffset.UtcNow)) };

        return response;
    }

    private async Task<T?> TryAsync<T>(string section, Func<Task<T>> build) where T : class
    {
        try
        {
            return await build();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Introspection section {Section} failed; leaving it out", section);
            return null;
        }
    }

    /// <summary>The sections <paramref name="request"/> asks for: the named ones, or every one.</summary>
    internal static HashSet<string> Wanted(AgentIntrospectionRequest request) =>
        request.Sections is { Count: > 0 } sections
            ? new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(IntrospectionSections.All, StringComparer.OrdinalIgnoreCase);

    /// <summary>The answer to <paramref name="request"/>, from the given state.</summary>
    internal static AgentIntrospectionResponse Build(
        AgentIntrospectionRequest request,
        string agentName,
        string agentVersion,
        DateTimeOffset? startedAt,
        LlmConfigSnapshot? llmConfig,
        LlmCostEstimator? costEstimator,
        LlmUsageLedger? usageLedger = null)
    {
        var wanted = Wanted(request);


        return new AgentIntrospectionResponse
        {
            AgentName = agentName,
            AgentVersion = agentVersion,
            StartedAt = startedAt,
            Models = wanted.Contains(IntrospectionSections.Models) && llmConfig is not null
                ? Models(llmConfig, costEstimator)
                : null,
            Pricing = wanted.Contains(IntrospectionSections.Pricing) && costEstimator is not null
                ? Pricing(costEstimator)
                : null,
            Usage = wanted.Contains(IntrospectionSections.Usage) && usageLedger is not null
                ? Usage(usageLedger.GetReport(request.ThreadId))
                : null,
        };
    }

    private static ModelsIntrospection Models(LlmConfigSnapshot snapshot, LlmCostEstimator? costEstimator) => new()
    {
        LoadedAt = snapshot.WrittenAt,
        Summary = snapshot.Describe(),
        FixedTier = snapshot.FixedTier,
        Note = snapshot.Note,
        Tiers = [.. snapshot.Tiers.Select(t => new TierIntrospection
        {
            Tier = t.Tier,
            UsesTier = t.UsesTier,
            AcceptsImages = t.AcceptsImages,
            Models = [.. t.Models.Select(m => new ModelIntrospection
            {
                Provider = m.Provider,
                Service = m.Service,
                EndpointHost = m.EndpointHost,
                ModelId = m.ModelId,
                SupportsImageInput = m.SupportsImageInput,
                ReasoningEffort = m.ReasoningEffort,
                Price = costEstimator?.FindEntry(m.ModelId) is { } entry ? Price(entry) : null,
            })],
        })],
        Embedding = snapshot.Embedding is { } e
            ? new EmbeddingIntrospection { ModelId = e.ModelId, Service = e.Service, EndpointHost = e.EndpointHost }
            : null,
        DecisionModel = snapshot.DecisionModel is { } d
            ? new DecisionModelIntrospection
            {
                Provider = d.Provider,
                Service = d.Service,
                EndpointHost = d.EndpointHost,
                Deployment = d.Deployment,
                DisabledUses = d.DisabledUses,
                Summary = d.Describe(),
            }
            : null,
    };

    private static PricingIntrospection Pricing(LlmCostEstimator costEstimator) => new()
    {
        LoadedFrom = costEstimator.LoadedFrom,
        Rows = [.. costEstimator.Entries.Select(Price)],
    };

    private static UsageIntrospection Usage(LlmUsageReport report) => new()
    {
        TimeZone = report.TimeZone,
        Since = report.Since,
        Today = report.Today,
        TodayTotals = Totals(report.TodayTotals),
        Last7Days = Totals(report.Last7Days),
        Last30Days = Totals(report.Last30Days),
        Days = [.. report.Days.Select(d => new DayUsageIntrospection { Date = d.Date, Totals = Totals(d.Totals) })],
        Models = [.. report.Models30Days.Select(m => new ModelUsageIntrospection { ModelId = m.ModelId, Totals = Totals(m.Totals) })],
        Kinds = [.. report.Kinds30Days.Select(k => new KindUsageIntrospection { Kind = k.Kind, Totals = Totals(k.Totals) })],
        Thread = report.Thread is { } t && report.ThreadId is { } id
            ? new ThreadUsageIntrospection { ThreadId = id, FirstAt = t.FirstAt, LastAt = t.LastAt, Totals = Totals(t.Totals) }
            : null,
    };

    private static UsageTotalsIntrospection Totals(LlmUsageTotals t) => new()
    {
        Requests = t.Requests,
        InputTokens = t.InputTokens,
        CachedInputTokens = t.CachedInputTokens,
        CacheWriteTokens = t.CacheWriteTokens,
        OutputTokens = t.OutputTokens,
        CostUsd = t.CostUsd,
        UnpricedRequests = t.UnpricedRequests,
    };

    private static PriceIntrospection Price(LlmPricingEntry entry) => new()
    {
        Prefix = entry.Prefix,
        InputPerM = entry.InputPerM,
        OutputPerM = entry.OutputPerM,
        CachedInputPerM = entry.CachedInputPerM,
        CacheWrite5mPerM = entry.CacheWrite5mPerM,
        CacheWrite1hPerM = entry.CacheWrite1hPerM,
    };

    private static DateTimeOffset GetProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
