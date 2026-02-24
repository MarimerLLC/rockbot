using System.ClientModel;
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
using RockBot.Scripts.Remote;
using RockBot.Agent;
using RockBot.Memory;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.A2A;
using RockBot.Subagent;
using RockBot.Tools.Scheduling;
using RockBot.Tools.Web;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (CreateApplicationBuilder only loads them in Development)
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

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
if (!tierOptions.Balanced.IsConfigured)
{
    tierOptions.Balanced.Endpoint = llmSection["Endpoint"];
    tierOptions.Balanced.ApiKey   = llmSection["ApiKey"];
    tierOptions.Balanced.ModelId  = llmSection["ModelId"];
}

if (tierOptions.Balanced.IsConfigured)
{
    IChatClient BuildClient(LlmTierConfig config)
    {
        return new OpenAIClient(
            new ApiKeyCredential(config.ApiKey!),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(config.Endpoint!),
                // Extend from the 100s default — subagents with large tool sets generate
                // longer responses that can exceed the default before the body is fully read.
                NetworkTimeout = TimeSpan.FromMinutes(5)
            })
            .GetChatClient(config.ModelId!).AsIChatClient();
    }

    // AddRockBotTieredChatClients must be called BEFORE AddModelBehaviors so that
    // its TryAddSingleton<ModelBehavior> (which uses the inner client closure directly)
    // wins over AddModelBehaviors' factory (which resolves IChatClient from DI and would
    // create a circular dependency: IChatClient → TieredChatClientRegistry → ModelBehavior
    // → IChatClient → deadlock).
    builder.Services.AddRockBotTieredChatClients(
        lowInnerClient:      BuildClient(tierOptions.Resolve(ModelTier.Low)),
        balancedInnerClient: BuildClient(tierOptions.Balanced),
        highInnerClient:     BuildClient(tierOptions.Resolve(ModelTier.High)));

    builder.Services.AddModelBehaviors(opts =>
        builder.Configuration.GetSection("ModelBehaviors").Bind(opts));

    builder.Services.AddSingleton<ILlmTierSelector, KeywordTierSelector>();
}
else
{
    builder.Services.AddRockBotChatClient(new EchoChatClient());
    builder.Services.AddSingleton<ILlmTierSelector>(_ => new FixedTierSelector(ModelTier.Balanced));
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Balanced:Endpoint (or legacy LLM:Endpoint), LLM:Balanced:ApiKey, and LLM:Balanced:ModelId to configure.");
}

// Tracks in-flight background tool loops so they can be cancelled when a new message arrives
builder.Services.AddSingleton<SessionBackgroundTaskTracker>();

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
    agent.WithIdentity("RockBot");
    agent.WithProfile();
    agent.WithRules();
    agent.WithMemory();
    agent.WithConversationLog();
    agent.WithFeedback();
    agent.WithSkills();
    agent.WithDreaming(opts =>
    {
        opts.Interval = TimeSpan.FromHours(4);
        opts.InitialDelay = TimeSpan.FromMinutes(5);
    });
    agent.AddToolHandler();
    agent.AddMcpToolProxy();
    agent.AddWebTools(opts => builder.Configuration.GetSection("WebTools").Bind(opts));
    agent.AddSchedulingTools();
    agent.AddHeartbeatBootstrap(opts =>
        builder.Configuration.GetSection("HeartbeatPatrol").Bind(opts));
    agent.AddSubagents();
    agent.AddA2ACaller(opts =>
    {
        var basePath = builder.Configuration["AgentProfile:BasePath"]
            ?? builder.Configuration["AgentProfile__BasePath"]
            ?? AppContext.BaseDirectory;
        opts.DirectoryPersistencePath = Path.Combine(basePath, "known-agents.json");

        // Well-known agents loaded from a JSON file on the PVC so the list can be
        // updated without rebuilding the image. File path mirrors the other agent
        // data files (soul.md, directives.md, etc.) under the agent base path.
        var wellKnownPath = Path.Combine(basePath, "well-known-agents.json");
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
                // Non-fatal — log at startup but don't prevent the agent from starting
                Console.Error.WriteLine($"[warn] Could not load well-known agents from {wellKnownPath}: {ex.Message}");
            }
        }
    });
    agent.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.HandleMessage<UserFeedback, UserFeedbackHandler>();
    agent.HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
    agent.SubscribeTo(UserProxyTopics.UserFeedback);
    agent.SubscribeTo(UserProxyTopics.ConversationHistoryRequest);
});

// Bind AgentProfileOptions from the AgentProfile config section so AgentProfile__BasePath
// (set in the k8s ConfigMap) overrides the default "agent" relative path → /data/agent (PVC).
builder.Services.Configure<AgentProfileOptions>(builder.Configuration.GetSection("AgentProfile"));

// MCP bridge (replaces external RockBot.Tools.Mcp.Bridge process)
builder.Services.Configure<McpBridgeOptions>(builder.Configuration.GetSection("McpBridge"));
builder.Services.AddHostedService<McpBridgeService>();

// Remote script runner — delegates script execution to the Script Manager pod via RabbitMQ
builder.Services.AddRemoteScriptRunner("RockBot");

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var chatClient = app.Services.GetRequiredService<IChatClient>();
var llmId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? chatClient.GetType().Name;
startupLogger.LogInformation("LLM (Balanced): {ModelId}", llmId);
var resolvedBehavior = app.Services.GetRequiredService<ModelBehavior>();
startupLogger.LogInformation(
    "ModelBehavior: NudgeOnHallucinatedToolCalls={Nudge}, AdditionalSystemPrompt={HasPrompt}, ScheduledTaskResultMode={ResultMode}",
    resolvedBehavior.NudgeOnHallucinatedToolCalls,
    resolvedBehavior.AdditionalSystemPrompt is not null ? "yes" : "no",
    resolvedBehavior.ScheduledTaskResultMode);
startupLogger.LogInformation("Listening for user messages on '{Topic}'", UserProxyTopics.UserMessage);

await app.RunAsync();
