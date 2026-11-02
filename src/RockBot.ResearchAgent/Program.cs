using System.ClientModel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using OpenAI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging.RabbitMQ;
using RockBot.ResearchAgent;
using RockBot.Tools;
using RockBot.Tools.Web;

// ResearchAgent: an MCP server with one tool, `research` (streamable HTTP, stateless, with the
// Tasks extension), plus the original A2A receiver, kept while callers move over
// (design/research-agent-mcp.md). A long-lived Deployment, not a one-shot ScaledJob.
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// ── LLM configuration — three-tier (Low / Balanced / High) ──────────────────
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

// Seed single Balanced from BalancedModels[0] so Low/High tier fallback resolution works
// when only the ordered fallback chain is configured.
if (!tierOptions.Balanced.IsConfigured && tierOptions.BalancedModels.Count > 0)
    tierOptions.Balanced = tierOptions.BalancedModels[0];

// ModelBehavior: raise iteration limit — research tasks routinely need more than the default 12
// to search, browse, cache, and then synthesise without hitting the wall mid-loop.
// Pre-registered before AddRockBotTieredChatClients so the TryAdd in that method respects this override.
builder.Services.AddSingleton(new ModelBehavior { MaxToolIterationsOverride = 50 });

if (tierOptions.Balanced.IsConfigured || tierOptions.BalancedModels.Count > 0)
{
    IChatClient BuildClient(LlmTierConfig config)
    {
        return new OpenAIClient(
            new ApiKeyCredential(config.ApiKey!),
            new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) })
            .GetChatClient(config.ModelId!).AsIChatClient();
    }

    // Build the balanced inner client: use FallbackChatClient when BalancedModels lists
    // multiple models (e.g. Azure Foundry primary, OpenRouter fallback).
    IChatClient balancedClient;
    if (tierOptions.BalancedModels.Count > 1)
    {
        var fallbackLoggerFactory = LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var fallbackLogger = fallbackLoggerFactory.CreateLogger<FallbackChatClient>();

        var entries = new List<(string ModelId, IChatClient Client)>();
        foreach (var cfg in tierOptions.BalancedModels)
            entries.Add((cfg.ModelId!, BuildClient(cfg)));

        balancedClient = new FallbackChatClient(entries, fallbackLogger);
    }
    else if (tierOptions.BalancedModels.Count == 1)
    {
        balancedClient = BuildClient(tierOptions.BalancedModels[0]);
    }
    else
    {
        balancedClient = BuildClient(tierOptions.Balanced);
    }

    // ResearchAgent uses Balanced (scoping pass, fallback synthesis) and High (main research loop).
    // Low is not needed — pass Balanced for the Low slot so it is never exercised.
    var highClient = BuildClient(tierOptions.Resolve(ModelTier.High));

    builder.Services.AddRockBotTieredChatClients(
        lowInnerClient:      balancedClient,
        balancedInnerClient: balancedClient,
        highInnerClient:     highClient);
}
else
{
    builder.Services.AddRockBotChatClient(new EchoChatClient());
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Balanced:Endpoint (or legacy LLM:Endpoint), LLM:Balanced:ApiKey, and LLM:Balanced:ModelId to configure.");
}

// AgentLoopRunner requires IFeedbackStore — use no-op since we have no dreaming pipeline
builder.Services.AddSingleton<IFeedbackStore, NullFeedbackStore>();

// AgentLoopRunner also depends on ISkillStore (used only on completion re-prompt,
// which we disable via enableFollowUp:false) and IConversationMemory (used only on
// Azure content-filter recovery). Null stores keep the server stateless apart from
// each run's own working memory.
builder.Services.AddSingleton<ISkillStore, NullSkillStore>();
builder.Services.AddSingleton<IConversationMemory, NullConversationMemory>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("ResearchAgent");

    // Working memory (in-memory TTL cache backed by file path Memory:BasePath, /tmp/memory in the pod).
    // Each run uses its own research/{runId} namespace and clears it when done.
    // Must come before AddWebTools — WebToolRegistrar depends on IWorkingMemory.
    agent.WithWorkingMemory();

    // Tool registry — required by WebToolRegistrar to register web tools.
    agent.AddToolHandler();

    // Web search + browse tools (Brave Search + HTTP page fetch)
    agent.AddWebTools(opts =>
    {
        opts.ApiKey = builder.Configuration["WebTools:ApiKey"] ?? string.Empty;
        opts.MaxSearchResults = 5;
        // Page chunks must outlive a long run (50 iterations can take well over the 20-minute
        // default); each run clears its namespace when it finishes, so this doesn't pile up.
        opts.ChunkTtlMinutes = Math.Max(opts.ChunkTtlMinutes, 60);
    });

    // A2A receiving side: declares queue, handles incoming AgentTaskRequest messages.
    // Kept during the migration to the MCP tool (the council still sends to
    // agent.task.ResearchAgent); Phase 5 removes it.
    //
    // The card is announced on the discovery bus only when Research:AnnounceOverA2A is true.
    // A long-lived pod re-announces every two minutes, so announcing would put ResearchAgent
    // back in the primary agent's A2A directory beside the MCP `research` tool. Off by default:
    // the queue is still consumed, but nobody discovers research over A2A.
    var announceOverA2A = builder.Configuration.GetValue<bool>("Research:AnnounceOverA2A");
    agent.AddA2A(opts =>
    {
        if (!announceOverA2A)
            return;

        opts.Card = new AgentCard
        {
            AgentName = "ResearchAgent",
            Description = "On-demand research agent. Searches the web, fetches pages, and synthesises answers using an LLM.",
            Version = "1.0",
            Skills =
            [
                new AgentSkill
                {
                    Id = "research",
                    Name = "Research",
                    Description = "Research a topic using web search and page fetching, then synthesise a concise answer."
                }
            ]
        };
    });

    agent.Services.AddScoped<IAgentTaskHandler, ResearchAgentTaskHandler>();
});

// ── Research core, shared by the MCP tool and the A2A receiver ──────────────
builder.Services.Configure<ResearchOptions>(builder.Configuration.GetSection(ResearchOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new ResearchConcurrencyGate(sp.GetRequiredService<IOptions<ResearchOptions>>().Value.MaxConcurrentRuns));
// Transient, like the AgentLoopRunner it wraps; the gate is the shared piece.
builder.Services.AddTransient<ResearchRunner>();
builder.Services.AddTransient<IResearchRunner>(sp => new GatedResearchRunner(
    sp.GetRequiredService<ResearchRunner>(), sp.GetRequiredService<ResearchConcurrencyGate>()));
builder.Services.AddSingleton<IResearchScoper, ResearchScoper>();

// Data Protection seals the clarifying question's requestState. The key ring stays in memory:
// with one replica, a restart only invalidates outstanding questions (the tool re-scopes).
builder.Services.AddDataProtection().SetApplicationName("RockBot.ResearchAgent");
builder.Services.AddSingleton(sp => new ResearchScopeToken(
    sp.GetRequiredService<IDataProtectionProvider>(),
    sp.GetRequiredService<IOptions<ResearchOptions>>().Value.ScopeTokenLifetime));

// ── MCP server: stateless streamable HTTP + Tasks ───────────────────────────
// One replica with an in-memory task store: a restart loses in-flight tasks, which the caller
// sees as a lost task and can retry. More replicas need a durable, shared IMcpTaskStore.
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTasks(new InMemoryMcpTaskStore { DefaultTimeToLive = TimeSpan.FromHours(2) },
        t => t.ExecutionModeSelector = ResearchCallMode.Select)
    .WithTools<ResearchTools>();

var app = builder.Build();

// Log crashes rather than silently dying — unhandled exceptions leave the A2A message unacked
// so it goes to the DLX. The caller's A2ATaskTracker timeout handles the resulting silence.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var crashLogger = app.Services.GetRequiredService<ILoggerFactory>()
                                  .CreateLogger("UnhandledException");
    crashLogger.LogCritical(e.ExceptionObject as Exception,
        "Unhandled exception — ResearchAgent exiting");
};

app.MapHealthChecks("/health");
app.MapMcp("/mcp");

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("ResearchAgent starting — MCP tool 'research' at /mcp, and A2A task requests");

await app.RunAsync();
