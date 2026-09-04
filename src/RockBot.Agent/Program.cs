using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging.RabbitMQ;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.ArgGuards;
using RockBot.Agent.McpBridge.Auth;
using RockBot.Scripts.Remote;
using RockBot.Agent;
using RockBot.Memory;
using RockBot.Observation;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.A2A;
using RockBot.ServiceSearch;
using RockBot.Subagent;
using RockBot.Subagent.Worker;
using RockBot.Wisp;
using RockBot.Llm.Copilot;
using GitHub.Copilot.SDK;
using RockBot.Tools.FileSystem;
using RockBot.Tools.Scheduling;
using RockBot.Tools.Web;
using RockBot.Telemetry;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (CreateApplicationBuilder only loads them in Development)
builder.Configuration.AddUserSecrets<Program>();

// Load optional appsettings.json from the PVC so cluster-side config (e.g. HeartbeatPatrol:CronExpression)
// can be changed without rebuilding the image. Layered after env vars so it takes precedence.
{
    var pvcBase = builder.Configuration["AgentProfile__BasePath"] ?? builder.Configuration["AgentProfile:BasePath"];
    if (pvcBase is not null)
        builder.Configuration.AddJsonFile(Path.Combine(pvcBase, "appsettings.json"), optional: true, reloadOnChange: false);
}

var agentName = builder.Configuration["Agent:Name"] ?? "RockBot";

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// OpenTelemetry — enabled via Telemetry:Enabled config key (set in k8s ConfigMap)
// ServiceName defaults to the agent name so multi-instance deployments are
// distinguishable in Grafana dashboards without extra config.
if (builder.Configuration.GetValue<bool>("Telemetry:Enabled"))
{
    builder.Services.AddRockBotTelemetry(opts =>
    {
        builder.Configuration.GetSection("Telemetry").Bind(opts);
        if (opts.ServiceName == "rockbot")
            opts.ServiceName = agentName;
    });
}

// ── LLM configuration — three-tier (Low / Balanced / High) ──────────────────
// Reads from the "LLM" config section. Three-tier keys:
//   LLM__Balanced__Endpoint, LLM__Balanced__ApiKey, LLM__Balanced__ModelId
//   LLM__Low__Endpoint, LLM__Low__ApiKey, LLM__Low__ModelId        (optional, falls back to Balanced)
//   LLM__High__Endpoint, LLM__High__ApiKey, LLM__High__ModelId     (optional, falls back to Balanced)
// Backward-compat flat keys (LLM__Endpoint, LLM__ApiKey, LLM__ModelId) are mapped to Balanced.
var llmSection = builder.Configuration.GetSection("LLM");

var tierOptions = new LlmTierOptions();
llmSection.Bind(tierOptions);

// Backward compat: flat LLM__{Endpoint/ApiKey/ModelId} → Balanced
// Only apply if the flat key is set AND the structured key isn't already populated.
if (!tierOptions.Balanced.IsConfigured)
{
    if (!string.IsNullOrEmpty(llmSection["Endpoint"]))
        tierOptions.Balanced.Endpoint = llmSection["Endpoint"];
    if (!string.IsNullOrEmpty(llmSection["ApiKey"]))
        tierOptions.Balanced.ApiKey   = llmSection["ApiKey"];
    if (!string.IsNullOrEmpty(llmSection["ModelId"]))
        tierOptions.Balanced.ModelId  = llmSection["ModelId"];
}

// If BalancedModels is used exclusively (no single Balanced key), seed Balanced from
// the first entry so that Low/High tier fallback resolution continues to work.
if (!tierOptions.Balanced.IsConfigured && tierOptions.BalancedModels.Count > 0)
    tierOptions.Balanced = tierOptions.BalancedModels[0];

// ── Per-tier client builder — each tier can use a different provider ─────────
// Global provider default: LLM__Provider (Copilot | empty = OpenAI-compatible)
// Per-tier override: LLM__Low__Provider, LLM__Balanced__Provider, LLM__High__Provider
var globalProvider = llmSection["Provider"];

// Lazy-initialized Copilot singleton — created only if at least one tier uses Copilot.
CopilotClient? copilotClient = null;
CopilotChatClientOptions? copilotBaseOptions = null;
ILoggerFactory? copilotLoggerFactory = null;
CopilotUsageTracker? copilotUsageTracker = null;
ICopilotSessionEvents? copilotSessionEvents = null;

async Task<CopilotClient> GetOrCreateCopilotClientAsync()
{
    if (copilotClient is not null)
        return copilotClient;

    copilotBaseOptions = new CopilotChatClientOptions();
    llmSection.Bind(copilotBaseOptions);
    copilotLoggerFactory = LoggerFactory.Create(b =>
        b.AddConsole().SetMinimumLevel(LogLevel.Information));

    // Usage tracker writes metrics to the shared data volume for introspection MCP.
    var basePath = builder.Configuration["AgentProfile:BasePath"] ?? "/data/agent";
    copilotUsageTracker = new CopilotUsageTracker(Path.Combine(basePath, "copilot-usage.json"));

    // Session events bridge — deferred resolution of IToolProgressNotifier from DI.
    copilotSessionEvents = new CopilotSessionEventsBridge();

    copilotClient = await CopilotClientFactory.CreateAndStartAsync(copilotBaseOptions);
    return copilotClient;
}

IChatClient BuildOpenAIClient(LlmTierConfig config)
{
    var clientOptions = new OpenAIClientOptions
    {
        Endpoint = new Uri(config.Endpoint!),
        // Extend from the 100s default — subagents with large tool sets generate
        // longer responses that can exceed the default before the body is fully read.
        NetworkTimeout = TimeSpan.FromMinutes(5)
    };

    // repetition_penalty has no ChatOptions equivalent, so it is injected into the
    // serialised body by a pipeline policy. Only registered when configured, so the
    // request shape is byte-identical to before for everyone who leaves it unset.
    var repetitionPenalty = builder.Configuration.GetValue<float?>("AgentHost:RepetitionPenalty");
    if (repetitionPenalty is > 0f)
    {
        clientOptions.AddPolicy(new RepetitionPenaltyPolicy(repetitionPenalty.Value),
            PipelinePosition.PerCall);
        // Logged because the failure mode is silent: an unbound or mistyped setting looks
        // exactly like a working one from the outside, and the symptom it treats (verbatim
        // looping) takes several turns to reappear.
        Console.WriteLine($"    repetition_penalty={repetitionPenalty.Value} (body-injected)");
    }

    // Reasoning effort is per-tier: a cheap Low tier and a deliberating High tier want
    // different budgets. Injected into the body for the same reason as repetition_penalty —
    // ChatOptions cannot express OpenRouter's nested reasoning object, and the flat
    // reasoning_effort field it *can* express is accepted and ignored by OpenRouter.
    var reasoningEffort = ReasoningEffortPolicy.Normalise(config.ReasoningEffort);
    if (reasoningEffort is not null)
    {
        clientOptions.AddPolicy(new ReasoningEffortPolicy(reasoningEffort),
            PipelinePosition.PerCall);
        Console.WriteLine($"    reasoning.effort={reasoningEffort} (body-injected)");
    }
    else if (!string.IsNullOrWhiteSpace(config.ReasoningEffort))
    {
        // Warned rather than thrown: the model still answers, it simply keeps its default
        // reasoning budget and the only visible effect is a bill that never came down.
        Console.WriteLine($"    WARNING: ignoring unrecognised ReasoningEffort " +
                          $"'{config.ReasoningEffort}' (expected minimal/low/medium/high/none)");
    }

    // OpenRouter attributes spend to whatever app the client names in its headers; without
    // them every call shows up as "unknown" on the activity dashboard. Registered only for
    // OpenRouter endpoints so the app name is not disclosed to unrelated providers.
    if (OpenRouterAttributionPolicy.IsOpenRouterEndpoint(config.Endpoint))
    {
        var appName = builder.Configuration["LLM:AppName"] is { Length: > 0 } n
            ? n : OpenRouterAttributionPolicy.DefaultAppName;
        var appUrl = builder.Configuration["LLM:AppUrl"] is { Length: > 0 } u
            ? u : OpenRouterAttributionPolicy.DefaultAppUrl;

        clientOptions.AddPolicy(
            new OpenRouterAttributionPolicy(appName, appUrl), PipelinePosition.PerCall);
        Console.WriteLine($"    OpenRouter attribution: {appName} <{appUrl}>");
    }

    return new OpenAIClient(new ApiKeyCredential(config.ApiKey!), clientOptions)
        .GetChatClient(config.ModelId!).AsIChatClient();
}

async Task<IChatClient> BuildClientForTierAsync(LlmTierConfig config, string tierName)
{
    if (config.IsCopilot(globalProvider))
    {
        var client = await GetOrCreateCopilotClientAsync();
        var modelId = config.ModelId ?? copilotBaseOptions!.ModelId;
        var opts = new CopilotChatClientOptions
        {
            ModelId = modelId,
            UseLoggedInUser = copilotBaseOptions!.UseLoggedInUser,
            GitHubToken = copilotBaseOptions.GitHubToken,
            RequestTimeout = copilotBaseOptions.RequestTimeout,
            MaxRetries = copilotBaseOptions.MaxRetries,
            RetryBaseDelay = copilotBaseOptions.RetryBaseDelay
        };
        Console.WriteLine($"  {tierName}: Copilot ({modelId})");
        return new CopilotChatClient(
            client, opts,
            copilotLoggerFactory!.CreateLogger<CopilotChatClient>(),
            copilotUsageTracker,
            copilotSessionEvents);
    }

    Console.WriteLine($"  {tierName}: OpenAI-compatible ({config.ModelId} @ {config.Endpoint})");
    return BuildOpenAIClient(config);
}

// Determine whether any tier is configured.
var anyConfigured = tierOptions.Balanced.IsConfigured
    || tierOptions.BalancedModels.Count > 0
    || !string.IsNullOrEmpty(globalProvider)
    || tierOptions.Low.IsCopilot(globalProvider)
    || tierOptions.High.IsCopilot(globalProvider);

if (anyConfigured)
{
    Console.WriteLine("LLM tier configuration:");

    // Build the balanced inner client: use FallbackChatClient when multiple models are listed.
    IChatClient balancedInner;
    if (tierOptions.BalancedModels.Count > 1)
    {
        var fallbackLoggerFactory = LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var fallbackLogger = fallbackLoggerFactory.CreateLogger<FallbackChatClient>();

        var entries = new List<(string ModelId, IChatClient Client)>();
        foreach (var cfg in tierOptions.BalancedModels)
            entries.Add((cfg.ModelId!, await BuildClientForTierAsync(cfg, $"Balanced[{entries.Count}]")));

        var agentHostOpts = new AgentHostOptions();
        builder.Configuration.GetSection("AgentHost").Bind(agentHostOpts);

        balancedInner = new FallbackChatClient(entries, fallbackLogger,
            perAttemptTimeout: agentHostOpts.LlmCallTimeout);
    }
    else if (tierOptions.BalancedModels.Count == 1)
    {
        balancedInner = await BuildClientForTierAsync(tierOptions.BalancedModels[0], "Balanced");
    }
    else
    {
        balancedInner = await BuildClientForTierAsync(tierOptions.Balanced, "Balanced");
    }

    var lowConfig = tierOptions.Resolve(ModelTier.Low);
    var highConfig = tierOptions.Resolve(ModelTier.High);

    // AddRockBotTieredChatClients must be called BEFORE AddModelBehaviors so that
    // its TryAddSingleton<ModelBehavior> (which uses the inner client closure directly)
    // wins over AddModelBehaviors' factory (which resolves IChatClient from DI and would
    // create a circular dependency: IChatClient → TieredChatClientRegistry → ModelBehavior
    // → IChatClient → deadlock).
    builder.Services.AddRockBotTieredChatClients(
        lowInnerClient:      await BuildClientForTierAsync(lowConfig, "Low"),
        balancedInnerClient: balancedInner,
        highInnerClient:     await BuildClientForTierAsync(highConfig, "High"));

    builder.Services.AddModelBehaviors(opts =>
        builder.Configuration.GetSection("ModelBehaviors").Bind(opts));

    // LLM:FixedTier pins every request to one tier, bypassing keyword classification.
    // Useful when tiers hold genuinely different models rather than quality grades of the
    // same one — e.g. a creative agent whose conversation must always use the model chosen
    // for its voice, while a background service points at another tier for a different job.
    var fixedTier = builder.Configuration["LLM:FixedTier"];
    if (!string.IsNullOrWhiteSpace(fixedTier)
        && Enum.TryParse<ModelTier>(fixedTier, ignoreCase: true, out var pinnedTier))
    {
        builder.Services.AddSingleton<ILlmTierSelector>(_ => new FixedTierSelector(pinnedTier));
        Console.WriteLine($"LLM tier selection pinned to {pinnedTier} (LLM:FixedTier)");
    }
    else
    {
        builder.Services.AddSingleton<ILlmTierSelector, KeywordTierSelector>();
    }
}
else
{
    builder.Services.AddRockBotChatClient(new EchoChatClient());
    builder.Services.AddSingleton<ILlmTierSelector>(_ => new FixedTierSelector(ModelTier.Balanced));
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Balanced:Endpoint (or legacy LLM:Endpoint), LLM:Balanced:ApiKey, and LLM:Balanced:ModelId to configure.");
}

// Tier routing logger — appends routing decisions to tier-routing-log.jsonl for dream self-correction
builder.Services.AddSingleton<TierRoutingLogger>();

// Optional text-embedding model for hybrid BM25 + vector search.
// When Embedding:Endpoint is set, stores use cosine similarity alongside BM25.
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embedding"));
var embeddingOptions = new EmbeddingOptions();
builder.Configuration.GetSection("Embedding").Bind(embeddingOptions);
if (embeddingOptions.IsConfigured)
{
    var embeddingClient = new OpenAIClient(
            new ApiKeyCredential(embeddingOptions.ApiKey ?? "unused"),
            new OpenAIClientOptions { Endpoint = new Uri(embeddingOptions.Endpoint!) })
        .GetEmbeddingClient(embeddingOptions.Model!)
        .AsIEmbeddingGenerator();

    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingClient);
    Console.WriteLine($"Embedding model configured: {embeddingOptions.Model} @ {embeddingOptions.Endpoint}");
}
else
{
    Console.WriteLine("No embedding config found — using BM25-only search.");
    Console.WriteLine("Set Embedding:Endpoint and Embedding:Model to enable hybrid vector search.");
}

// Register memory tools as singleton — AIFunction instances are built once at construction
builder.Services.AddSingleton<MemoryTools>();
// Rules tools — requires WithRules() in the agent builder below
builder.Services.AddSingleton<RulesTools>();
// Tracks which memory IDs have been injected per session, enabling delta recall across topic shifts
builder.Services.AddSingleton<InjectedMemoryTracker>();
// Skill index tracker (SkillTools is created per-session in UserMessageHandler)
builder.Services.AddSingleton<SkillIndexTracker>();
builder.Services.AddSingleton<SkillRecallTracker>();
// Tool guides for memory and skill subsystems
builder.Services.AddSingleton<IToolSkillProvider, MemoryToolSkillProvider>();
builder.Services.AddSingleton<IToolSkillProvider, SkillToolSkillProvider>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity(agentName);
    agent.WithProfile();
    agent.WithRules();
    agent.WithMemory();
    agent.WithConversationMemory(opts => builder.Configuration.GetSection("ConversationMemory").Bind(opts));
    agent.WithConversationLog();
    agent.WithFeedback(opts => builder.Configuration.GetSection("Feedback").Bind(opts));
    agent.WithSkills();
    agent.WithKnowledgeGraph();
    agent.WithFailureClusterStore();
    agent.WithRepairTickets();
    agent.WithDreaming(opts => builder.Configuration.GetSection("Dream").Bind(opts));
    agent.WithMemoryAudit(opts => builder.Configuration.GetSection("MemoryAudit").Bind(opts));
    agent.AddToolHandler();
    // The proxy must outwait the bridge: the bridge caps a tool call at MaxTimeoutMs
    // (15 min) and answers with a timeout response of its own. If the proxy gave up
    // first the caller would see a transport failure instead of that response.
    //
    // Configurable because the response wait is also how long the agent blocks when the
    // bridge does not answer at all (process down, message lost) — a deployment with no
    // slow MCP servers will want that far below the 930s default.
    var mcpProxySection = builder.Configuration.GetSection("McpToolProxy");
    agent.AddMcpToolProxy(
        requestTimeout: TimeSpan.FromSeconds(
            mcpProxySection.GetValue("RequestTimeoutSeconds", 60)),
        responseTimeout: TimeSpan.FromSeconds(
            mcpProxySection.GetValue("ResponseTimeoutSeconds", 930)));
    agent.AddFileSystemTools(opts => builder.Configuration.GetSection("FileSystem").Bind(opts));
    agent.AddWebTools(opts => builder.Configuration.GetSection("WebTools").Bind(opts));
    agent.AddSchedulingTools();
    agent.AddHeartbeatBootstrap(opts =>
        builder.Configuration.GetSection("HeartbeatPatrol").Bind(opts));
    agent.AddSubagents();
    agent.AddWorkers();
    agent.AddWisps(opts =>
        opts.SharedVolumePath = builder.Configuration["FileSystem:BasePath"] ?? "/rockbot/shared");
    var a2aBasePath = builder.Configuration["AgentProfile:BasePath"]
        ?? builder.Configuration["AgentProfile__BasePath"]
        ?? AppContext.BaseDirectory;
    agent.AddA2A(opts =>
    {
        opts.Card = new AgentCard
        {
            AgentName = agentName,
            Description = "Personal AI agent — accepts notifications and availability queries",
            Version = "1.0",
            Skills =
            [
                new AgentSkill { Id = "notify-user", Name = "Notify User",
                    Description = "Send a notification to the user" },
                new AgentSkill { Id = "query-availability", Name = "Query Availability",
                    Description = "Check if the user is available (free/busy)" },
                new AgentSkill { Id = "negotiate-meeting", Name = "Negotiate Meeting",
                    Description = "Multi-turn meeting negotiation — proposes available times and confirms when the caller selects one" }
            ]
        };
        opts.TrustStorePath = Path.Combine(a2aBasePath, "agent-trust.json");

        // Shared options — AddA2A registers A2AOptions first (AddSingleton);
        // AddA2ACaller uses TryAddSingleton so it reuses this instance.
        opts.DirectoryPersistencePath = Path.Combine(a2aBasePath, "known-agents.json");

        // Well-known agents loaded from a JSON file on the PVC so the list can be
        // updated without rebuilding the image.
        var wellKnownPath = Path.Combine(a2aBasePath, "well-known-agents.json");
        if (File.Exists(wellKnownPath))
        {
            try
            {
                var json = File.ReadAllText(wellKnownPath);
                var cards = System.Text.Json.JsonSerializer.Deserialize<List<AgentCard>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cards is { Count: > 0 })
                    opts.WellKnownAgents = cards;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] Could not load well-known agents from {wellKnownPath}: {ex.Message}");
            }
        }
    });
    agent.Services.AddScoped<IAgentTaskHandler, RockBot.Agent.A2A.RockBotTaskHandler>();
    agent.AddA2ACaller();
    agent.AddServiceSearch();
    agent.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.HandleMessage<UserFeedback, UserFeedbackHandler>();
    agent.HandleMessage<CancelSessionRequest, CancelSessionHandler>();
    agent.HandleMessage<ClearContextRequest, ClearContextHandler>();
    agent.HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>();
    agent.HandleMessage<AgentInfoRequest, AgentInfoRequestHandler>();
    agent.HandleMessage<ActiveStatusRequest, ActiveStatusRequestHandler>();
    agent.WithSavedResponses();
    agent.HandleMessage<SaveResponseRequest, SaveResponseRequestHandler>();
    agent.HandleMessage<ListSavedResponsesRequest, ListSavedResponsesRequestHandler>();
    agent.HandleMessage<GetSavedResponseRequest, GetSavedResponseRequestHandler>();
    agent.HandleMessage<DeleteSavedResponseRequest, DeleteSavedResponseRequestHandler>();
    agent.SubscribeTo($"{UserProxyTopics.UserMessage}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.UserFeedback}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.CancelSession}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.ClearContext}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.ConversationHistoryRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.AgentInfoRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.ActiveStatusRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.SaveResponseRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.ListSavedResponsesRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.GetSavedResponseRequest}.{agentName}");
    agent.SubscribeTo($"{UserProxyTopics.DeleteSavedResponseRequest}.{agentName}");
});

// Bind AgentProfileOptions from the AgentProfile config section so AgentProfile__BasePath
// (set in the k8s ConfigMap) overrides the default "agent" relative path → /data/agent (PVC).
builder.Services.Configure<AgentProfileOptions>(builder.Configuration.GetSection("AgentProfile"));

// Allow MaxToolIterations and other AgentHostOptions to be overridden via appsettings.json or env vars.
builder.Services.Configure<AgentHostOptions>(builder.Configuration.GetSection("AgentHost"));

// LLM pricing — loaded from a JSON file on the agent PVC so prices can be refreshed
// without rebuilding the image. LlmPricing__ConfigPath in the ConfigMap overrides the default.
builder.Services.Configure<LlmPricingOptions>(builder.Configuration.GetSection("LlmPricing"));

// Shared attachments storage — the filesystem facade over ${ROCKBOT_SHARED_PATH}/attachments.
// Used by the attach_image reply tool to validate model-named files before referencing them.
// McpBridgeService keeps its own Lazy instance; this registration limits blast radius.
builder.Services.AddSingleton<RockBot.Agent.McpBridge.Attachments.IAttachmentStorage,
    RockBot.Agent.McpBridge.Attachments.AttachmentStorage>();

// MCP bridge (replaces external RockBot.Tools.Mcp.Bridge process)
builder.Services.Configure<McpBridgeOptions>(builder.Configuration.GetSection("McpBridge"));
builder.Services.AddSingleton(new McpArgGuardRegistration(
    PathPrefixArgGuard.HandlerName, new PathPrefixArgGuard()));
builder.Services.AddSingleton<IMcpArgGuardRegistry, McpArgGuardRegistry>();
builder.Services.AddHostedService<McpBridgeService>();

// WorkIQ auth (MSAL token provider + cache store) — registered only when
// the host has been configured with an Entra tenant and client ID. Keeping
// this conditional means deployments without WorkIQ never pull MSAL into
// the runtime and never publish/subscribe on auth.workiq.* topics.
if (!string.IsNullOrWhiteSpace(builder.Configuration["WorkIQ:TenantId"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["WorkIQ:ClientId"]))
{
    builder.Services.AddWorkIqAuth(builder.Configuration);
}

// Remote script runner — delegates script execution to the Script Manager pod via RabbitMQ
builder.Services.AddRemoteScriptRunner("RockBot");

// Observation framework — registers core services (state store, extractor,
// evaluator, pipeline phases, coordinator) plus the default theory-of-self
// and theory-of-user targets. The dream cycle's RunObservationPassAsync
// drives the pipeline once per dream when DreamOptions.ObservationEnabled
// is true. Markdown copies are written under {profile}/observation/ for
// inspection; promoted theories are also published as long-term memory
// entries (category="observation/theory/{name}") so SearchMemory finds them.
{
    var profileBasePath = builder.Configuration["AgentProfile:BasePath"]
        ?? builder.Configuration["AgentProfile__BasePath"]
        ?? "agent";
    var resolvedProfileBasePath = Path.IsPathRooted(profileBasePath)
        ? profileBasePath
        : Path.Combine(AppContext.BaseDirectory, profileBasePath);

    // The observation/theory pipeline clusters proposed observations by vector
    // similarity, so it hard-requires an IEmbeddingGenerator. Register it only
    // when embeddings are configured; otherwise DreamService's optional
    // coordinator stays null and the observation pass is skipped — keeping the
    // BM25-only path startable, as documented.
    if (embeddingOptions.IsConfigured)
    {
        builder.Services.AddRockBotObservation();
        builder.Services.AddDefaultObservationTargets(resolvedProfileBasePath);
    }
    else
    {
        Console.WriteLine("No embedding config — observation/theory dream pass disabled (BM25-only mode).");
    }
}

var app = builder.Build();

// Wire the deferred service provider for Copilot session event bridge.
(copilotSessionEvents as CopilotSessionEventsBridge)?.SetServiceProvider(app.Services);

await app.RunAsync();
