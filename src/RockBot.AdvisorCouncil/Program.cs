using System.ClientModel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using OpenAI;
using RockBot.AdvisorCouncil;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Llm.Anthropic;
using RockBot.Messaging.InProcess;

// AdvisorCouncil: an MCP server with one tool, `advise` (streamable HTTP, stateless, with the
// Tasks extension). A long-lived Deployment (design/advisor-council-mcp.md). The A2A path, its
// KEDA ScaledJob and its RabbitMQ queues were retired in Phase 6, so it talks to no broker.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);

// The agent host still wants a bus (AgentHost subscribes), but nothing outside this process
// listens: an in-process one, with no broker to reach.
builder.Services.AddRockBotInProcessMessaging();

// ── Council options ─────────────────────────────────────────────────────────
builder.Services.Configure<CouncilOptions>(builder.Configuration.GetSection("Council"));

// ── LLM configuration — three-tier (Low / Balanced / High) ──────────────────
// Reads the same LLM__* settings as the primary agent (rockbot-secrets), so it must cope with
// what the primary agent's config holds: OpenAI-compatible endpoints and the native Anthropic
// provider (LLM__Provider / per-entry Provider). Copilot is not supported here; an entry this
// agent can't build is skipped with a warning rather than failing startup. Same builder as the
// research server's (src/RockBot.ResearchAgent/Program.cs).
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

// Per-persona tool-iteration cap (research path). Pre-registered before
// AddRockBotTieredChatClients so its TryAdd respects this override.
builder.Services.AddSingleton(new ModelBehavior { MaxToolIterationsOverride = 8 });

IChatClient? TryBuildClient(LlmTierConfig config, string name)
{
    if (config.IsCopilot(globalProvider))
    {
        Console.WriteLine($"  {name}: Copilot ({config.ModelId}) is not supported by AdvisorCouncil; skipped");
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
            // Synthesis on the High tier can take minutes; the SDK default is 100 s.
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
    // The council uses Balanced (select, personas, critique) and High (synthesis).
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

builder.Services.AddSingleton<IFeedbackStore, NullFeedbackStore>();

// ── Council components ──────────────────────────────────────────────────────
builder.Services.AddSingleton<PersonaRegistry>();
builder.Services.AddHostedService<PersonaRegistryHotReload>();
// Each step gets the chat client of its tier (Council:SelectTier, PersonaTier, SynthesisTier).
// The plain IChatClient is Balanced, which is what every step used to get, synthesis included.
IChatClient ClientFor(IServiceProvider sp, Func<CouncilOptions, ModelTier> tier) =>
    sp.GetService<TieredChatClientRegistry>()?.GetClient(tier(sp.GetRequiredService<IOptions<CouncilOptions>>().Value))
    ?? sp.GetRequiredService<IChatClient>(); // the EchoChatClient, when no LLM is configured

builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SelectStep>(sp, ClientFor(sp, o => o.SelectTier)));
builder.Services.AddSingleton<PreResearchStep>();
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<PersonaStep>(sp, ClientFor(sp, o => o.PersonaTier)));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<CritiqueStep>(sp, ClientFor(sp, o => o.PersonaTier)));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SynthesizeStep>(sp, ClientFor(sp, o => o.SynthesisTier)));
builder.Services.AddSingleton<CouncilOrchestrator>();
builder.Services.AddSingleton(sp =>
    new CouncilConcurrencyGate(sp.GetRequiredService<IOptions<CouncilOptions>>().Value.MaxConcurrentRuns));
builder.Services.AddSingleton<ICouncil>(sp => new GatedCouncil(
    sp.GetRequiredService<CouncilOrchestrator>(), sp.GetRequiredService<CouncilConcurrencyGate>()));
// Research over MCP (design/research-agent-mcp.md, Phase 3): the research server's `research`
// tool, run as a task; its clarifying question is answered by the llm responder from the call's
// own arguments.
builder.Services.AddSingleton<IResearchClient, ResearchMcpClient>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("AdvisorCouncil");

    // Working memory — persona branches with research write their findings here under
    // council/{runId}/{personaId}; namespace shared across personas for cross-pollination.
    // Each run clears council/{runId}/ when it ends. Bump the per-namespace cap above the
    // default 50: one run can produce ~5 personas × (1 view + 3 research + 1 revised) + 1
    // shared = ~26 entries, and MaxConcurrentRuns runs share the pod.
    agent.WithWorkingMemory(opts => opts.MaxEntriesPerNamespace = 200);
});

// Data Protection seals the clarifying question's requestState. The key ring stays in memory:
// with one replica, a restart only invalidates outstanding questions (the tool selects again).
builder.Services.AddDataProtection().SetApplicationName("RockBot.AdvisorCouncil");
builder.Services.AddSingleton(sp => new CouncilClarificationToken(
    sp.GetRequiredService<IDataProtectionProvider>(),
    sp.GetRequiredService<IOptions<CouncilOptions>>().Value.ClarificationTokenLifetime));

// ── MCP server: stateless streamable HTTP + Tasks ───────────────────────────
// One replica with an in-memory task store: a restart loses in-flight councils, which the
// caller sees as a lost task and can retry. More replicas need a durable, shared IMcpTaskStore.
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTasks(new InMemoryMcpTaskStore { DefaultTimeToLive = TimeSpan.FromHours(2) },
        t => t.ExecutionModeSelector = CouncilCallMode.Select)
    .WithTools<CouncilTools>();

var app = builder.Build();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var crashLogger = app.Services.GetRequiredService<ILoggerFactory>()
                                  .CreateLogger("UnhandledException");
    crashLogger.LogCritical(e.ExceptionObject as Exception,
        "Unhandled exception — AdvisorCouncil exiting");
};

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var registry = app.Services.GetRequiredService<PersonaRegistry>();
startupLogger.LogInformation(
    "AdvisorCouncil: {Count} personas loaded from {Path} (hash {Hash})",
    registry.Personas.Count, registry.PersonasPath, registry.PersonaSetHash[..8]);

app.MapHealthChecks("/health");
app.MapMcp("/mcp");

startupLogger.LogInformation("AdvisorCouncil starting — MCP tool 'advise' at /mcp");

await app.RunAsync();
