using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging.RabbitMQ;
using RockBot.ResearchAgent;
using RockBot.Tools;
using RockBot.Tools.Web;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// Configure LLM chat client
var llmConfig = builder.Configuration.GetSection("LLM");
var endpoint = llmConfig["Endpoint"];
var apiKey = llmConfig["ApiKey"];
var modelId = llmConfig["ModelId"];

if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(modelId))
{
    var openAiClient = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

    builder.Services.AddSingleton<IChatClient>(
        openAiClient.GetChatClient(modelId).AsIChatClient());
}
else
{
    builder.Services.AddSingleton<IChatClient, EchoChatClient>();
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Endpoint, LLM:ApiKey, and LLM:ModelId to configure.");
}

// ModelBehavior: raise iteration limit — research tasks routinely need more than the default 12
// to search, browse, cache, and then synthesise without hitting the wall mid-loop.
builder.Services.AddSingleton(new ModelBehavior { MaxToolIterationsOverride = 50 });

// AgentLoopRunner requires IFeedbackStore — use no-op since we have no dreaming pipeline
builder.Services.AddSingleton<IFeedbackStore, NullFeedbackStore>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("ResearchAgent");

    // Working memory (in-memory TTL cache backed by ephemeral file path /tmp/memory).
    // Must come before AddWebTools — WebToolRegistrar depends on IWorkingMemory.
    agent.WithWorkingMemory();

    // Tool registry — required by WebToolRegistrar to register web tools.
    agent.AddToolHandler();

    // Web search + browse tools (Brave Search + HTTP page fetch)
    agent.AddWebTools(opts =>
    {
        opts.ApiKey = builder.Configuration["WebTools:ApiKey"] ?? string.Empty;
        opts.MaxSearchResults = 5;
    });

    // A2A receiving side: declares queue, handles incoming AgentTaskRequest messages
    agent.AddA2A(opts =>
    {
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

    // Graceful one-shot shutdown: pod exits after one task completes
    agent.Services.AddSingleton<EphemeralShutdownCoordinator>();
    agent.Services.AddHostedService<EphemeralShutdownService>();
});

var app = builder.Build();

// Log crashes rather than silently dying — unhandled exceptions leave the message unacked
// so it goes to the DLX. The caller's A2ATaskTracker timeout handles the resulting silence.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var crashLogger = app.Services.GetRequiredService<ILoggerFactory>()
                                  .CreateLogger("UnhandledException");
    crashLogger.LogCritical(e.ExceptionObject as Exception,
        "Unhandled exception — ResearchAgent exiting");
};

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("ResearchAgent starting — waiting for A2A task requests");

await app.RunAsync();
