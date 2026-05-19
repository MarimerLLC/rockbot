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
using RockBot.Messaging.RabbitMQ;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// ── Council options ─────────────────────────────────────────────────────────
builder.Services.Configure<CouncilOptions>(builder.Configuration.GetSection("Council"));

// ── LLM configuration — three-tier (Low / Balanced / High) ──────────────────
var llmSection = builder.Configuration.GetSection("LLM");
var tierOptions = new LlmTierOptions();
llmSection.Bind(tierOptions);

if (!tierOptions.Balanced.IsConfigured)
{
    tierOptions.Balanced.Endpoint = llmSection["Endpoint"];
    tierOptions.Balanced.ApiKey   = llmSection["ApiKey"];
    tierOptions.Balanced.ModelId  = llmSection["ModelId"];
}
if (!tierOptions.Balanced.IsConfigured && tierOptions.BalancedModels.Count > 0)
    tierOptions.Balanced = tierOptions.BalancedModels[0];

// Per-persona tool-iteration cap (Phase 3 — research path). Pre-registered before
// AddRockBotTieredChatClients so its TryAdd respects this override.
builder.Services.AddSingleton(new ModelBehavior { MaxToolIterationsOverride = 8 });

if (tierOptions.Balanced.IsConfigured || tierOptions.BalancedModels.Count > 0)
{
    IChatClient BuildClient(LlmTierConfig config) =>
        new OpenAIClient(
            new ApiKeyCredential(config.ApiKey!),
            new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint!) })
            .GetChatClient(config.ModelId!).AsIChatClient();

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
builder.Services.AddSingleton<ResearchAgentInvoker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResearchAgentInvoker>());

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
