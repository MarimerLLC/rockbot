using System.ClientModel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using OpenAI;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Llm.Anthropic;
using RockBot.Messaging.InProcess;
using RockBot.ResearchAgent;
using RockBot.Tools;
using RockBot.Tools.Web;

// ResearchAgent: an MCP server with one tool, `research` (streamable HTTP, stateless, with the
// Tasks extension). A long-lived Deployment (design/research-agent-mcp.md). The A2A path it used
// to serve was retired in Phase 5, so it talks to no message broker.
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// The agent host still wants a bus (AgentHost subscribes, tool progress publishes), but nothing
// outside this process listens any more: an in-process one, with no broker to reach.
builder.Services.AddRockBotInProcessMessaging();

// ── LLM configuration — three-tier (Low / Balanced / High) ──────────────────
// Reads the same LLM__* settings as the primary agent (rockbot-secrets), so it must cope with
// what the primary agent's config holds: OpenAI-compatible endpoints and the native Anthropic
// provider (LLM__Provider / per-entry Provider). Copilot is not supported here; an entry this
// server can't build is skipped with a warning rather than failing startup.
var llmSection = builder.Configuration.GetSection("LLM");

var tierOptions = new LlmTierOptions();
llmSection.Bind(tierOptions);

var globalProvider = llmSection["Provider"];
var anthropicOptions = new AnthropicProviderOptions();
llmSection.GetSection("Anthropic").Bind(anthropicOptions);

// Backward compat: flat LLM__{Endpoint/ApiKey/ModelId} → Balanced
if (!tierOptions.Balanced.IsConfigured)
{
    tierOptions.Balanced.Endpoint ??= llmSection["Endpoint"];
    tierOptions.Balanced.ApiKey   ??= llmSection["ApiKey"];
    tierOptions.Balanced.ModelId  ??= llmSection["ModelId"];
}

// Seed single Balanced from BalancedModels[0] so Low/High tier fallback resolution works
// when only the ordered fallback chain is configured.
if (!tierOptions.Balanced.IsConfigured && tierOptions.BalancedModels.Count > 0)
    tierOptions.Balanced = tierOptions.BalancedModels[0];

// ModelBehavior: raise iteration limit — research tasks routinely need more than the default 12
// to search, browse, cache, and then synthesise without hitting the wall mid-loop.
// Pre-registered before AddRockBotTieredChatClients so the TryAdd in that method respects this override.
builder.Services.AddSingleton(new ModelBehavior { MaxToolIterationsOverride = 50 });

IChatClient? TryBuildClient(LlmTierConfig config, string name)
{
    if (config.IsCopilot(globalProvider))
    {
        Console.WriteLine($"  {name}: Copilot ({config.ModelId}) is not supported by ResearchAgent; skipped");
        return null;
    }

    if (config.IsAnthropic(globalProvider))
    {
        if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ModelId))
        {
            Console.WriteLine($"  {name}: Anthropic needs both ApiKey and ModelId; skipped");
            return null;
        }
        Console.WriteLine($"  {name}: Anthropic ({config.ModelId})");
        return AnthropicChatClientFactory.Create(
            config.ApiKey, config.ModelId, config.Endpoint, config.ReasoningEffort,
            anthropicOptions, timeout: TimeSpan.FromMinutes(5));
    }

    if (!config.IsConfigured)
    {
        Console.WriteLine($"  {name}: needs Endpoint, ApiKey and ModelId; skipped");
        return null;
    }

    Console.WriteLine($"  {name}: OpenAI-compatible ({config.ModelId} @ {config.Endpoint})");
    return new OpenAIClient(
        new ApiKeyCredential(config.ApiKey!),
        new OpenAIClientOptions
        {
            Endpoint = new Uri(config.Endpoint!),
            // Long research prompts on the High tier can take minutes; the SDK default is 100 s.
            NetworkTimeout = TimeSpan.FromMinutes(5),
        })
        .GetChatClient(config.ModelId!).AsIChatClient();
}

// Build the balanced inner client: use FallbackChatClient when BalancedModels lists
// multiple models (e.g. Azure Foundry primary, OpenRouter fallback).
IChatClient? balancedClient = null;
var balancedEntries = new List<(string ModelId, IChatClient Client)>();
for (var i = 0; i < tierOptions.BalancedModels.Count; i++)
{
    var cfg = tierOptions.BalancedModels[i];
    if (TryBuildClient(cfg, $"BalancedModels[{i}]") is { } client)
        balancedEntries.Add((cfg.ModelId ?? $"balanced-{i}", client));
}

if (balancedEntries.Count > 1)
{
    var fallbackLoggerFactory = LoggerFactory.Create(b =>
        b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    balancedClient = new FallbackChatClient(balancedEntries, fallbackLoggerFactory.CreateLogger<FallbackChatClient>());
}
else if (balancedEntries.Count == 1)
{
    balancedClient = balancedEntries[0].Client;
}
else
{
    balancedClient = TryBuildClient(tierOptions.Balanced, "Balanced");
}

if (balancedClient is not null)
{
    // ResearchAgent uses Balanced (scoping pass, fallback synthesis) and High (main research loop).
    // Low is not needed — pass Balanced for the Low slot so it is never exercised.
    var highClient = TryBuildClient(tierOptions.Resolve(ModelTier.High), "High") ?? balancedClient;

    builder.Services.AddRockBotTieredChatClients(
        lowInnerClient:      balancedClient,
        balancedInnerClient: balancedClient,
        highInnerClient:     highClient);
}
else
{
    builder.Services.AddRockBotChatClient(new EchoChatClient());
    Console.WriteLine("No usable LLM config found — using EchoChatClient.");
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
});

// ── Research core ────────────────────────────────────────────────────────────
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

// Log crashes rather than silently dying.
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
startupLogger.LogInformation("ResearchAgent starting — MCP tool 'research' at /mcp");

await app.RunAsync();
