using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// Extension methods for registering agent memory systems.
/// </summary>
public static class AgentMemoryExtensions
{
    /// <summary>
    /// Registers conversation memory, long-term memory, and working memory with default options.
    /// </summary>
    public static AgentHostBuilder WithMemory(this AgentHostBuilder builder)
    {
        builder.WithConversationMemory();
        builder.WithLongTermMemory();
        builder.WithWorkingMemory();
        return builder;
    }

    /// <summary>
    /// Registers conversation memory with optional configuration.
    /// Sessions are persisted to disk so history survives agent restarts.
    /// </summary>
    public static AgentHostBuilder WithConversationMemory(
        this AgentHostBuilder builder,
        Action<ConversationMemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<ConversationMemoryOptions>(_ => { });

        builder.Services.AddSingleton<InMemoryConversationMemory>();
        builder.Services.AddSingleton<FileConversationMemory>();
        builder.Services.AddSingleton<IConversationMemory>(sp => sp.GetRequiredService<FileConversationMemory>());
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileConversationMemory>());

        return builder;
    }

    /// <summary>
    /// Registers file-based long-term memory with optional configuration.
    /// </summary>
    public static AgentHostBuilder WithLongTermMemory(
        this AgentHostBuilder builder,
        Action<MemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<MemoryOptions>(_ => { });

        builder.Services.TryAddSingleton<EmbeddingTextPreparer>();
        builder.Services.AddSingleton<ILongTermMemory, FileMemoryStore>();

        // Phase 2 self-repair: capability-claim writer and read-side verifier.
        // Both are internal services — not exposed as LLM tools.
        builder.Services.AddSingleton<ICapabilityClaimWriter, CapabilityClaimWriter>();
        builder.Services.AddSingleton<ICapabilityClaimVerifier, CapabilityClaimVerifier>();

        // Phase 3 self-repair: hot-path contradiction detector. Narrowly scoped to
        // claim/capability/* and feedback/* writes; other categories short-circuit.
        builder.Services.AddSingleton<IMemoryContradictionDetector, MemoryContradictionDetector>();

        return builder;
    }

    /// <summary>
    /// Registers session-scoped working memory (TTL-based scratch space for tool call results).
    /// </summary>
    public static AgentHostBuilder WithWorkingMemory(
        this AgentHostBuilder builder,
        Action<WorkingMemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<WorkingMemoryOptions>(_ => { });

        builder.Services.TryAddSingleton<EmbeddingTextPreparer>();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<HybridCacheWorkingMemory>();
        builder.Services.AddSingleton<FileWorkingMemory>();
        builder.Services.AddSingleton<IWorkingMemory>(sp => sp.GetRequiredService<FileWorkingMemory>());
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileWorkingMemory>());

        return builder;
    }

    /// <summary>
    /// Registers the file-based skill store with optional configuration.
    /// </summary>
    public static AgentHostBuilder WithSkills(
        this AgentHostBuilder builder,
        Action<SkillOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<SkillOptions>(_ => { });

        builder.Services.TryAddSingleton<EmbeddingTextPreparer>();
        builder.Services.AddSingleton<ISkillStore, FileSkillStore>();
        builder.Services.AddSingleton<ISkillUsageStore, FileSkillUsageStore>();
        builder.Services.AddSingleton<ISkillResourceUsageStore, FileSkillResourceUsageStore>();
        builder.Services.Configure<ToolCallLogOptions>(_ => { });
        builder.Services.AddSingleton<IToolCallLog, FileToolCallLog>();
        builder.Services.AddSingleton<IHostedService, StarterSkillService>();

        return builder;
    }

    /// <summary>
    /// Registers the periodic memory consolidation service (dreaming).
    /// Requires <see cref="ILongTermMemory"/> and <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// to be registered — call after <see cref="WithLongTermMemory"/> and the chat client setup.
    /// </summary>
    public static AgentHostBuilder WithDreaming(
        this AgentHostBuilder builder,
        Action<DreamOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<DreamOptions>(_ => { });

        builder.Services.AddSingleton<DreamService>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DreamService>());

        return builder;
    }

    /// <summary>
    /// Registers the file-based conversation log, enabling the preference-inference dream pass.
    /// Call after <see cref="WithConversationMemory"/> and before <see cref="WithDreaming"/>.
    /// <see cref="WithMemory"/> does NOT call this — callers opt in explicitly.
    /// </summary>
    public static AgentHostBuilder WithConversationLog(
        this AgentHostBuilder builder,
        Action<ConversationLogOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<ConversationLogOptions>(_ => { });

        builder.Services.AddSingleton<IConversationLog, FileConversationLog>();

        // Adapter that exposes the conversation log to the observation
        // framework. Registered alongside IConversationLog so its lifecycle
        // tracks the log; DreamService takes it as an optional dependency
        // and skips the observation pass when this is absent.
        builder.Services.AddSingleton<ConversationLogTranscriptAdapter>();

        return builder;
    }

    /// <summary>
    /// Registers the file-based saved-response store with optional configuration.
    /// </summary>
    public static AgentHostBuilder WithSavedResponses(
        this AgentHostBuilder builder,
        Action<SavedResponseOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<SavedResponseOptions>(_ => { });

        builder.Services.AddSingleton<ISavedResponseStore, FileSavedResponseStore>();

        return builder;
    }

    /// <summary>
    /// Registers the feedback capture system: <see cref="IFeedbackStore"/> (file-backed) and
    /// the <see cref="SessionSummaryService"/> background evaluator.
    /// Requires <see cref="IConversationMemory"/> and an LLM client to be registered.
    /// </summary>
    public static AgentHostBuilder WithFeedback(
        this AgentHostBuilder builder,
        Action<FeedbackOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<FeedbackOptions>(_ => { });

        builder.Services.AddSingleton<IFeedbackStore, FileFeedbackStore>();
        builder.Services.AddSingleton<SessionSummaryService>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionSummaryService>());

        return builder;
    }

    /// <summary>
    /// Registers the in-process, PVC-backed failure cluster store. Records every
    /// post-recovery MCP tool failure so DreamService can spot recurring patterns
    /// and open repair tickets. Opt-in — call after <see cref="WithMemory"/>.
    /// See <c>design/self-repair.md</c> Phase 5.
    /// </summary>
    public static AgentHostBuilder WithFailureClusterStore(
        this AgentHostBuilder builder,
        Action<FailureClusterOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<FailureClusterOptions>(_ => { });

        builder.Services.AddSingleton<FileFailureClusterStore>();
        builder.Services.AddSingleton<IFailureClusterStore>(sp => sp.GetRequiredService<FileFailureClusterStore>());
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileFailureClusterStore>());

        return builder;
    }

    /// <summary>
    /// Registers the closed-loop repair-ticket pipeline (Phase 4 self-repair):
    /// the file-backed <see cref="IRepairTicketStore"/>, the four
    /// <see cref="IRepairTargetApplier"/> implementations, and the cache-free
    /// <see cref="IRepairTicketVerifier"/>. Opt-in — call after
    /// <see cref="WithFailureClusterStore"/>, <see cref="WithMemory"/>,
    /// <see cref="WithSkills"/>, and <see cref="WithWorkingMemory"/>.
    /// See <c>design/self-repair.md</c> Phase 4.
    /// </summary>
    public static AgentHostBuilder WithRepairTickets(
        this AgentHostBuilder builder,
        Action<RepairTicketOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<RepairTicketOptions>(_ => { });

        builder.Services.AddSingleton<IRepairTicketStore, FileRepairTicketStore>();
        builder.Services.AddSingleton<IRepairTargetApplier, SkillBodyApplier>();
        builder.Services.AddSingleton<IRepairTargetApplier, SkillResourceApplier>();
        builder.Services.AddSingleton<IRepairTargetApplier, WorkingMemoryEvictApplier>();
        builder.Services.AddSingleton<IRepairTargetApplier, ToolDefaultRegisterApplier>();
        builder.Services.AddSingleton<IRepairTargetApplier, PromptBuilderHintApplier>();
        builder.Services.AddSingleton<IRepairTicketVerifier, RepairTicketVerifier>();

        return builder;
    }

    /// <summary>
    /// Registers the file-backed knowledge graph store for entity-relationship reasoning.
    /// </summary>
    public static AgentHostBuilder WithKnowledgeGraph(
        this AgentHostBuilder builder,
        Action<KnowledgeGraphOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<KnowledgeGraphOptions>(_ => { });

        builder.Services.AddSingleton<IKnowledgeGraph, FileKnowledgeGraphStore>();

        return builder;
    }
}
