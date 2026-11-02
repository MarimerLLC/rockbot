using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.A2A;
using RockBot.AdvisorCouncil;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Llm.Anthropic;
using RockBot.Messaging.RabbitMQ;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

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
builder.Services.AddSingleton<SelectStep>();
builder.Services.AddSingleton<PreResearchStep>();
builder.Services.AddSingleton<PersonaStep>();
builder.Services.AddSingleton<CritiqueStep>();
builder.Services.AddSingleton<SynthesizeStep>();
builder.Services.AddSingleton<CouncilOrchestrator>();
// Research over MCP (design/research-agent-mcp.md, Phase 3): the research server's `research`
// tool, run as a task; its clarifying question is answered by the llm responder from the call's
// own arguments.
builder.Services.AddSingleton<IResearchClient, ResearchMcpClient>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("AdvisorCouncil");

    // Working memory — persona branches with research write their findings here under
    // council/{taskId}/{personaId}; namespace shared across personas for cross-pollination.
    // Bump per-namespace cap above the default 50: a single council run can produce
    // ~5 personas × (1 view + 3 research + 1 revised) + 1 shared = ~26 entries, with headroom.
    agent.WithWorkingMemory(opts => opts.MaxEntriesPerNamespace = 200);

    agent.AddA2A(opts =>
    {
        opts.Card = new AgentCard
        {
            AgentName = "AdvisorCouncil",
            Description = "Multi-perspective advisor council. Analyzes questions from a curated set of personas and returns integrated guidance.",
            Version = "1.0",
            Skills =
            [
                new AgentSkill
                {
                    Id = "advise",
                    Name = "Advise",
                    Description = "Take a question or idea and return multi-perspective analysis with synthesis."
                }
            ]
        };
    });

    agent.Services.AddScoped<IAgentTaskHandler, AdvisorCouncilTaskHandler>();

    agent.Services.AddSingleton<EphemeralShutdownCoordinator>();
    agent.Services.AddHostedService<EphemeralShutdownService>();
});

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
    "AdvisorCouncil starting — {Count} personas loaded from {Path} (hash {Hash})",
    registry.Personas.Count, registry.PersonasPath, registry.PersonaSetHash[..8]);
startupLogger.LogInformation("AdvisorCouncil waiting for A2A task requests");

await app.RunAsync();
