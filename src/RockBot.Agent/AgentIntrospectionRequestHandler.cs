using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="AgentIntrospectionRequest"/> (#834): describes the agent's configuration
/// (its models, its pricing table) from the process's own state, so a user interface can show it
/// without an LLM round trip. Deterministic, no LLM invocation; read-only.
/// </summary>
internal sealed class AgentIntrospectionRequestHandler(
    IMessagePublisher publisher,
    AgentNameHolder agentNameHolder,
    ILogger<AgentIntrospectionRequestHandler> logger,
    LlmConfigSnapshot? llmConfig = null,
    LlmCostEstimator? costEstimator = null) : IMessageHandler<AgentIntrospectionRequest>
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
            costEstimator);

        var envelope = response.ToEnvelope<AgentIntrospectionResponse>(
            source: context.Agent.Name,
            correlationId: context.Envelope.CorrelationId);

        await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

        logger.LogDebug("Published agent introspection: models={Models} pricing={Pricing}",
            response.Models is not null, response.Pricing is not null);
    }

    /// <summary>The answer to <paramref name="request"/>, from the given state.</summary>
    internal static AgentIntrospectionResponse Build(
        AgentIntrospectionRequest request,
        string agentName,
        string agentVersion,
        DateTimeOffset? startedAt,
        LlmConfigSnapshot? llmConfig,
        LlmCostEstimator? costEstimator)
    {
        var wanted = request.Sections is { Count: > 0 } sections
            ? new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(IntrospectionSections.All, StringComparer.OrdinalIgnoreCase);

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
